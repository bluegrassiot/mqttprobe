using Microsoft.Extensions.Logging;
using MqttProbe.Core.Models.Mqtt;

namespace MqttProbe.Core.Services.Security;

public class MauiCertificateAssetStore : ICertificateAssetStore
{
    private readonly ICertificateAssetStore _store;
    private readonly ICertificateAssetImportPipeline _pipeline;
    private readonly ICertificateEnvelopeKeyStore _envelopeKeyStore;
    private readonly IFileProtector _fileProtector;
    private readonly ILogger<MauiCertificateAssetStore> _logger;
    private readonly string _certificatesDirectory;

    public MauiCertificateAssetStore(
        ICertificateAssetStore store,
        ICertificateAssetImportPipeline pipeline,
        ICertificateEnvelopeKeyStore envelopeKeyStore,
        string certificatesDirectory,
        IFileProtector fileProtector,
        ILogger<MauiCertificateAssetStore> logger)
    {
        _store = store;
        _pipeline = pipeline;
        _envelopeKeyStore = envelopeKeyStore;
        _certificatesDirectory = certificatesDirectory;
        _fileProtector = fileProtector;
        _logger = logger;
    }

    public string CertificatesDirectory => _certificatesDirectory;

    public async Task<string> ImportAsync(Guid ownerConnectionId, CertificateImportRequest request)
    {
        var iosRequest = request with { SkipCanonicalExport = true };
        var (assetId, tempPath) = await _pipeline.ImportStagedAsync(ownerConnectionId, iosRequest);

        if (!_fileProtector.ApplyProtections(tempPath))
        {
            await CleanupStagedOrQuarantineAsync(tempPath, assetId, "iOS file protections failed");
            throw new CertificateImportException("Failed to apply iOS file protections to certificate asset.");
        }

        try
        {
            return await _pipeline.PublishAsync(assetId, tempPath);
        }
        catch (Exception ex)
        {
            await CleanupStagedOrQuarantineAsync(tempPath, assetId, $"PublishAsync failed: {ex.Message}");
            throw new CertificateImportException("Failed to publish certificate asset after applying protections.", ex);
        }
    }

    public Task<ClientCertificateBundle?> LoadAsync(Guid ownerConnectionId, string assetId)
        => _store.LoadAsync(ownerConnectionId, assetId);

    public Task DeleteAsync(Guid ownerConnectionId, string assetId)
        => _store.DeleteAsync(ownerConnectionId, assetId);

    public Task<IReadOnlyList<(Guid OwnerId, string AssetId)>> ListAssetsAsync()
        => _store.ListAssetsAsync();

    public async Task<string?> DuplicateAsync(Guid sourceOwnerId, string sourceAssetId, Guid targetOwnerId)
    {
        var staged = await _pipeline.DuplicateStagedAsync(sourceOwnerId, sourceAssetId, targetOwnerId);
        if (staged is null) return null;

        var (newAssetId, tempPath) = staged.Value;

        if (!_fileProtector.ApplyProtections(tempPath))
        {
            await CleanupStagedOrQuarantineAsync(tempPath, newAssetId, "iOS file protections failed on duplicate");
            return null;
        }

        try
        {
            return await _pipeline.PublishAsync(newAssetId, tempPath);
        }
        catch (Exception ex)
        {
            await CleanupStagedOrQuarantineAsync(tempPath, newAssetId, $"PublishAsync failed on duplicate: {ex.Message}");
            return null;
        }
    }

    private async Task CleanupStagedOrQuarantineAsync(string tempPath, string assetId, string reason)
    {
        bool tempDeleted = _fileProtector.TryDelete(tempPath);

        if (!tempDeleted)
        {
            var quarantinePath = Path.Combine(_certificatesDirectory, $"cert-{assetId}.quarantine");
            _fileProtector.TryDelete(quarantinePath);
            bool renamed = _fileProtector.TryMoveToQuarantine(tempPath, quarantinePath);

            if (!renamed)
            {
                var retryMarker = Path.Combine(_certificatesDirectory, $"cert-{assetId}.cleanup-retry");
                try { await File.WriteAllTextAsync(retryMarker, reason); } catch { /* marker is only a retry hint; the LogCritical below is the real signal */ }
                _logger.LogCritical(
                    "CRITICAL: Could not delete or quarantine staged temp {Path}. " +
                    "Cleanup retry scheduled via {Marker}. Reason: {Reason}",
                    tempPath, retryMarker, reason);
            }
            else
            {
                _logger.LogError("Could not delete staged temp {Path}; quarantined. Reason: {Reason}",
                    tempPath, reason);
            }
        }

        try { await _envelopeKeyStore.RemoveAsync($"cert-env-{assetId}"); } catch { /* an envelope key with no blob is inert; startup cleanup sweeps it */ }
    }
}
