using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using MqttProbe.Core.Models.Mqtt;

namespace MqttProbe.Core.Services.Security;

public sealed class CertificateAssetStore : ICertificateAssetStore, ICertificateAssetImportPipeline
{
    private readonly ICertificateEnvelopeKeyStore _envelopeKeyStore;
    private readonly ILogger<CertificateAssetStore> _logger;

    public string CertificatesDirectory { get; }

    public CertificateAssetStore(
        ICertificateEnvelopeKeyStore envelopeKeyStore,
        string certificatesDirectory,
        ILogger<CertificateAssetStore> logger)
    {
        _envelopeKeyStore = envelopeKeyStore;
        _logger = logger;
        CertificatesDirectory = Path.Combine(certificatesDirectory, "certificates");
        Directory.CreateDirectory(CertificatesDirectory);
    }

    public async Task<string> ImportAsync(Guid ownerConnectionId, CertificateImportRequest request)
    {
        var (assetId, tempPath) = await ImportStagedAsync(ownerConnectionId, request);
        return await PublishAsync(assetId, tempPath);
    }

    public async Task<(string AssetId, string TempPath)> ImportStagedAsync(
        Guid ownerConnectionId, CertificateImportRequest request)
    {
        var (pfxBytes, internalPassword) = CertificateImportConverter.Import(request);
        var assetId = Guid.NewGuid().ToString("D");
        var encryptionKey = RandomNumberGenerator.GetBytes(32);
        var header = CertificateAssetBlobCodec.BuildHeader(assetId, ownerConnectionId);
        var aad = CertificateAssetBlobCodec.BuildAad(
            assetId, assetId, ownerConnectionId.ToString("D"), CertificateAssetBlobCodec.Version);
        var encrypted = CertificateAssetBlobCodec.Encrypt(encryptionKey, pfxBytes, aad);
        var blob = CertificateAssetBlobCodec.Assemble(header, encrypted);
        var tempPath = Path.Combine(CertificatesDirectory, $"cert-{assetId}.bin.tmp");

        try
        {
            await File.WriteAllBytesAsync(tempPath, blob);
            RestrictFilePermissions(tempPath);

            var envelopeJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                v = CertificateAssetBlobCodec.Version,
                k = Convert.ToBase64String(encryptionKey),
                p = internalPassword
            });
            await _envelopeKeyStore.SetAsync(CertificateAssetBlobCodec.EnvelopeKey(assetId), envelopeJson);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Certificate staging failed after crypto (os={OS}): {ExceptionType}: {Message}. " +
                "Thrown while persisting blob or envelope key (envelope store type={EnvelopeStore}).",
                System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                ex.GetType().FullName, ex.Message, _envelopeKeyStore.GetType().FullName);
            TryDelete(tempPath);
            try { await _envelopeKeyStore.RemoveAsync(CertificateAssetBlobCodec.EnvelopeKey(assetId)); } catch { /* best-effort rollback; the staging failure above is rethrown */ }
            throw;
        }

        return (assetId, tempPath);
    }

    public async Task<string> PublishAsync(string assetId, string tempPath)
    {
        ValidateAssetId(assetId);
        var finalPath = Path.Combine(CertificatesDirectory, $"cert-{assetId}.bin");
        try
        {
            RestrictFilePermissions(tempPath);
            File.Move(tempPath, finalPath);
            return assetId;
        }
        catch
        {
            TryDelete(tempPath);
            try { await _envelopeKeyStore.RemoveAsync(CertificateAssetBlobCodec.EnvelopeKey(assetId)); } catch { /* best-effort rollback; the publish failure is reported below */ }
            throw new CertificateImportException("Failed to publish certificate asset.");
        }
    }

    public async Task<ClientCertificateBundle?> LoadAsync(Guid ownerConnectionId, string assetId)
    {
        ValidateAssetId(assetId);
        var path = Path.Combine(CertificatesDirectory, $"cert-{assetId}.bin");
        if (!File.Exists(path)) return null;

        byte[] blob;
        try { blob = await File.ReadAllBytesAsync(path); } catch { return null; }
        if (!CertificateAssetBlobCodec.HasMinimumBlobLength(blob)) return null;

        if (!CertificateAssetBlobCodec.TryParseHeader(blob, out var header)
            || !Guid.TryParse(header.AssetId, out var parsedAssetId)
            || parsedAssetId.ToString("D") != assetId
            || !Guid.TryParse(header.Owner, out var parsedOwner)
            || parsedOwner != ownerConnectionId)
            return null;

        if (!CertificateAssetBlobCodec.TrySplit(blob, out var parts)) return null;

        var envelope = await ReadEnvelopeAsync(assetId);
        if (envelope is null) return null;
        var (encryptionKey, internalPassword) = envelope.Value;
        var aad = CertificateAssetBlobCodec.BuildAad(
            assetId, header.AssetId, header.Owner, header.Version);
        if (!CertificateAssetBlobCodec.TryDecrypt(encryptionKey, parts, aad, out var pfxBytes)) return null;

        return LoadBundleFromPfx(pfxBytes, internalPassword);
    }

    private async Task<byte[]?> ReadEnvelopeKeyForDeleteAsync(string assetId)
    {
        string? envelopeJson;
        try
        {
            envelopeJson = await _envelopeKeyStore.GetAsync(CertificateAssetBlobCodec.EnvelopeKey(assetId));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cannot read envelope for {AssetId}; skipping delete", assetId);
            return null;
        }
        if (envelopeJson is null) return null;

        try
        {
            var envelope = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(envelopeJson);
            return Convert.FromBase64String(envelope.GetProperty("k").GetString()!);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Malformed envelope for {AssetId}; skipping delete", assetId);
            return null;
        }
    }

    private async Task<(byte[] Key, string Password)?> ReadEnvelopeAsync(string assetId)
    {
        string? envelopeJson;
        try
        {
            envelopeJson = await _envelopeKeyStore.GetAsync(CertificateAssetBlobCodec.EnvelopeKey(assetId));
        }
        catch { return null; }
        if (envelopeJson is null) return null;

        try
        {
            var envelope = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(envelopeJson);
            var keyB64 = envelope.GetProperty("k").GetString()
                ?? throw new InvalidOperationException("Missing 'k' property");
            var internalPassword = envelope.GetProperty("p").GetString()
                ?? throw new InvalidOperationException("Missing 'p' property");
            return (Convert.FromBase64String(keyB64), internalPassword);
        }
        catch { return null; }
    }

    private static ClientCertificateBundle? LoadBundleFromPfx(byte[] pfxBytes, string internalPassword)
    {
        try
        {
            var loadFlags = GetClientCertificateLoadFlags(
                OperatingSystem.IsWindows(),
                OperatingSystem.IsMacOS());
            var cert = X509CertificateLoader.LoadPkcs12(pfxBytes, internalPassword, loadFlags);
            if (!cert.HasPrivateKey) { cert.Dispose(); return null; }
            return new ClientCertificateBundle(cert);
        }
        catch { return null; }
    }

    public async Task DeleteAsync(Guid ownerConnectionId, string assetId)
    {
        ValidateAssetId(assetId);
        var path = Path.Combine(CertificatesDirectory, $"cert-{assetId}.bin");
        if (!File.Exists(path)) return;

        byte[] blob;
        try { blob = await File.ReadAllBytesAsync(path); } catch { return; }
        if (!CertificateAssetBlobCodec.HasMinimumBlobLength(blob)) return;

        if (!CertificateAssetBlobCodec.TryParseHeader(blob, out var header)
            || !Guid.TryParse(header.AssetId, out var parsedAssetId)
            || parsedAssetId.ToString("D") != assetId
            || !Guid.TryParse(header.Owner, out var parsedOwner)
            || parsedOwner != ownerConnectionId)
            return;

        if (!CertificateAssetBlobCodec.TrySplit(blob, out var parts)) return;

        var encryptionKey = await ReadEnvelopeKeyForDeleteAsync(assetId);
        if (encryptionKey is null) return;

        var aad = CertificateAssetBlobCodec.BuildAad(
            assetId, header.AssetId, header.Owner, header.Version);
        if (!CertificateAssetBlobCodec.TryVerify(encryptionKey, parts, aad))
        {
            _logger.LogWarning("Tampered blob {AssetId}; not deleting", assetId);
            return;
        }

        if (TryDelete(path))
        {
            try { await _envelopeKeyStore.RemoveAsync(CertificateAssetBlobCodec.EnvelopeKey(assetId)); } catch { /* an envelope key with no blob is inert; startup cleanup sweeps it */ }
        }
    }

    public async Task<IReadOnlyList<(Guid OwnerId, string AssetId)>> ListAssetsAsync()
    {
        var results = new List<(Guid, string)>();
        if (!Directory.Exists(CertificatesDirectory))
            return results;

        foreach (var file in Directory.EnumerateFiles(CertificatesDirectory, "cert-*.bin"))
        {
            var name = Path.GetFileName(file);
            if (name.EndsWith(".tmp", StringComparison.Ordinal)
                || name.EndsWith(".quarantine", StringComparison.Ordinal)
                || name.EndsWith(".cleanup-retry", StringComparison.Ordinal))
                continue;

            try
            {
                var blob = await File.ReadAllBytesAsync(file);
                if (!CertificateAssetBlobCodec.HasMinimumBlobLength(blob)
                    || !CertificateAssetBlobCodec.TryParseHeader(blob, out var header)
                    || !Guid.TryParse(header.AssetId, out var assetGuid)
                    || !Guid.TryParse(header.Owner, out var ownerGuid))
                    continue;

                var fileNameAssetId = name["cert-".Length..^".bin".Length];
                if (fileNameAssetId != header.AssetId) continue;
                if (!CertificateAssetBlobCodec.TrySplit(blob, out var parts)) continue;

                var envelopeJson = await _envelopeKeyStore.GetAsync(
                    CertificateAssetBlobCodec.EnvelopeKey(assetGuid.ToString("D")));
                if (envelopeJson is null) continue;

                var envelope = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(envelopeJson);
                var encryptionKey = Convert.FromBase64String(envelope.GetProperty("k").GetString()!);
                var aad = CertificateAssetBlobCodec.BuildAad(
                    assetGuid.ToString("D"), header.AssetId, header.Owner, header.Version);
                if (!CertificateAssetBlobCodec.TryVerify(encryptionKey, parts, aad)) continue;

                results.Add((ownerGuid, assetGuid.ToString("D")));
            }
            catch { /* unreadable or undecryptable blob; omit it rather than fail the whole listing */ }
        }
        return results;
    }

    private static void RestrictFilePermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static bool TryDelete(string path)
    {
        try { File.Delete(path); return true; } catch { return false; }
    }

    private static void ValidateAssetId(string assetId)
    {
        if (!Guid.TryParse(assetId, out _))
            throw new CertificateImportException(
                $"Invalid certificate asset ID '{assetId}': must be a valid GUID.");
    }

    internal static X509KeyStorageFlags GetClientCertificateLoadFlags(
        bool isWindows,
        bool isMacOs) =>
        isWindows || isMacOs
            ? X509KeyStorageFlags.DefaultKeySet
            : X509KeyStorageFlags.EphemeralKeySet;
}
