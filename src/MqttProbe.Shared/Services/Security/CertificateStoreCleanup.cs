using Microsoft.Extensions.Logging;
using MqttProbe.Models.Mqtt;

namespace MqttProbe.Services.Security;

// Filesystem hygiene for the certificate directory, run once at startup after the config
// is loaded. Lives outside SettingsStore because it is not a settings concern; it needs the
// connection list only to decide which assets are still referenced.
public sealed class CertificateStoreCleanup(
    ICertificateAssetStore certStore,
    ICertificateEnvelopeKeyStore envelopeKeyStore,
    ILogger<CertificateStoreCleanup>? logger = null,
    TimeProvider? timeProvider = null)
{
    // Injectable so tests can age a quarantine file by moving the clock instead of
    // back-dating the file: File.SetCreationTime does not portably set birth time on Linux,
    // and CI runs on ubuntu-latest.
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    // Stage order is load-bearing: staging temps go before their retry markers, and both
    // before the orphan sweep, which would otherwise see half-written assets as orphans.
    public async Task RunAsync(
        IReadOnlyList<Connection> knownConnections, bool configLoadedSuccessfully)
    {
        if (!Directory.Exists(certStore.CertificatesDirectory))
            return;

        // Staging cleanup ALWAYS runs regardless of config state:

        // 1. Delete staging files (.bin.tmp).
        await DeleteStagingFilesAsync();

        // 2. Delete cleanup-retry markers.
        await DeleteCleanupRetryMarkersAsync();

        // 3. Delete quarantine files older than 1 hour.
        await DeleteAgedQuarantineFilesAsync();

        // Orphan + AEAD cleanup ONLY when config loaded successfully
        if (!configLoadedSuccessfully)
        {
            logger?.LogWarning(
                "Config file was missing or corrupt; skipping orphan and AEAD verification cleanup. " + // DevSkim: ignore DS187371
                "Staging temp files, retry markers, and aged quarantine files were still cleaned. " +
                "Verified certificate assets were preserved. Re-import certificates if needed.");
            return;
        }

        var knownPairs = await certStore.ListAssetsAsync();
        await DeleteUnconfiguredAssetsAsync(knownConnections, knownPairs);
        await SweepUnverifiedBlobsAsync(knownConnections, knownPairs);
    }

    private async Task DeleteStagingFilesAsync()
    {
        foreach (var tmpFile in Directory.EnumerateFiles(certStore.CertificatesDirectory, "cert-*.bin.tmp"))
        {
            var tmpAssetId = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(tmpFile))
                ["cert-".Length..];
            try
            {
                File.Delete(tmpFile);
                try { await envelopeKeyStore.RemoveAsync($"cert-env-{tmpAssetId}"); } catch { /* best-effort; retried next startup */ }
                var staleMarker = Path.Combine(certStore.CertificatesDirectory, $"cert-{tmpAssetId}.cleanup-retry");
                if (File.Exists(staleMarker))
                    try { File.Delete(staleMarker); } catch { /* best-effort; retried next startup */ }
            }
            catch
            {
                await QuarantineStagingFileAsync(tmpFile, tmpAssetId);
            }
        }
    }

    // Last resort for a staging temp that could not be deleted: move it aside so it is never
    // mistaken for a live asset, and if even that fails, leave a marker and shout.
    private async Task QuarantineStagingFileAsync(string tmpFile, string tmpAssetId)
    {
        var quarantinePath = Path.Combine(certStore.CertificatesDirectory, $"cert-{tmpAssetId}.quarantine");
        try { File.Delete(quarantinePath); } catch { /* stale quarantine file; the move below overwrites or fails loudly */ }
        var renamed = false;
        try { File.Move(tmpFile, quarantinePath); renamed = true; } catch { /* handled via the renamed flag below */ }
        if (!renamed)
        {
            var retryMarker = Path.Combine(certStore.CertificatesDirectory, $"cert-{tmpAssetId}.cleanup-retry");
            if (!File.Exists(retryMarker))
                try { await File.WriteAllTextAsync(retryMarker, $"staging cleanup failed at {DateTime.UtcNow:o}"); } catch { /* marker is only a retry hint; the LogCritical below is the real signal */ }
            logger?.LogCritical(
                "Could not delete or quarantine staging temp {Path}. Cleanup retry scheduled.",
                tmpFile);
        }
        else
        {
            logger?.LogWarning("Could not delete staging temp {Path}; quarantined.", tmpFile);
            try { await envelopeKeyStore.RemoveAsync($"cert-env-{tmpAssetId}"); } catch { /* best-effort; retried next startup */ }
        }
    }

    private async Task DeleteCleanupRetryMarkersAsync()
    {
        foreach (var marker in Directory.EnumerateFiles(certStore.CertificatesDirectory, "cert-*.cleanup-retry"))
        {
            var markerAssetId = Path.GetFileNameWithoutExtension(marker)["cert-".Length..];
            var correspondingTmp = Path.Combine(certStore.CertificatesDirectory, $"cert-{markerAssetId}.bin.tmp");
            if (!File.Exists(correspondingTmp))
            {
                try { File.Delete(marker); } catch { /* best-effort; retried next startup */ }
                try { await envelopeKeyStore.RemoveAsync($"cert-env-{markerAssetId}"); } catch { /* best-effort; retried next startup */ }
            }
        }
    }

    private async Task DeleteAgedQuarantineFilesAsync()
    {
        foreach (var qFile in Directory.EnumerateFiles(certStore.CertificatesDirectory, "cert-*.quarantine"))
        {
            try
            {
                var creationTime = File.GetCreationTime(qFile);
                if (creationTime < _timeProvider.GetLocalNow().DateTime.AddHours(-1))
                {
                    File.Delete(qFile);
                    var qAssetId = Path.GetFileNameWithoutExtension(qFile)["cert-".Length..];
                    try { await envelopeKeyStore.RemoveAsync($"cert-env-{qAssetId}"); } catch { /* best-effort; retried next startup */ }
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to process quarantine file {Path}", qFile);
            }
        }
    }

    private async Task DeleteUnconfiguredAssetsAsync(
        IReadOnlyList<Connection> knownConnections,
        IReadOnlyList<(Guid OwnerId, string AssetId)> knownPairs)
    {
        var configuredPairs = knownConnections
            .Where(c => c.ClientCertificateAssetId is not null)
            .Select(c => (c.Id, c.ClientCertificateAssetId!))
            .ToHashSet();
        foreach (var (ownerId, assetId) in knownPairs)
        {
            if (!configuredPairs.Contains((ownerId, assetId)))
            {
                try { await certStore.DeleteAsync(ownerId, assetId); } catch { /* orphan sweep is best-effort; retried next startup */ }
            }
        }
    }

    // Blobs ListAssetsAsync could not verify: delete them unless the header names a connection
    // we still know about, in which case preserve and shout rather than destroy something the
    // user may still be able to recover.
    private async Task SweepUnverifiedBlobsAsync(
        IReadOnlyList<Connection> knownConnections,
        IReadOnlyList<(Guid OwnerId, string AssetId)> knownPairs)
    {
        var verifiedAssetIds = knownPairs.Select(p => p.AssetId).ToHashSet(StringComparer.Ordinal);
        var knownOwnerIds = knownConnections.Select(c => c.Id).ToHashSet();
        foreach (var binFile in Directory.EnumerateFiles(certStore.CertificatesDirectory, "cert-*.bin"))
        {
            var fileName = Path.GetFileName(binFile);
            if (fileName.EndsWith(".tmp", StringComparison.Ordinal)
                || fileName.EndsWith(".quarantine", StringComparison.Ordinal)
                || fileName.EndsWith(".cleanup-retry", StringComparison.Ordinal))
                continue;
            var fileAssetId = fileName["cert-".Length..^".bin".Length];
            if (verifiedAssetIds.Contains(fileAssetId))
                continue;

            try
            {
                var blob = await File.ReadAllBytesAsync(binFile);
                if (blob.Length < 73) { File.Delete(binFile); continue; }
                var headerOwner = System.Text.Encoding.ASCII.GetString(blob, 36, 36);
                if (Guid.TryParse(headerOwner, out var parsedOwner) && knownOwnerIds.Contains(parsedOwner))
                {
                    logger?.LogCritical(
                        "Certificate blob {Path} has known owner {OwnerId} but failed AEAD verification. " + // DevSkim: ignore DS187371
                        "Preserving; re-import or manually delete.", binFile, parsedOwner);
                }
                else
                {
                    File.Delete(binFile);
                    try { await envelopeKeyStore.RemoveAsync($"cert-env-{fileAssetId}"); } catch { /* best-effort; retried next startup */ }
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to process unverified blob {Path}", binFile);
            }
        }
    }
}
