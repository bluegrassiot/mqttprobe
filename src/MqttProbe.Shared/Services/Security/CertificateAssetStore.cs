using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging;
using MqttProbe.Models.Mqtt;

namespace MqttProbe.Services.Security;

public sealed class CertificateAssetStore : ICertificateAssetStore, ICertificateAssetImportPipeline
{
    private const string OidRsa = "1.2.840.113549.1.1.1";
    private const string OidEcc = "1.2.840.10045.2.1";
    private const int Version = 1;
    private const int NonceLength = 12;
    private const int TagLength = 16;

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
        ValidateRequest(request);

        byte[] pfxBytes;
        string internalPassword;

        if (request.Mode == CertificateInputMode.Pfx)
        {
            (pfxBytes, internalPassword) = ImportPfx(request);
        }
        else
        {
            (pfxBytes, internalPassword) = ImportPem(request);
        }

        var assetId = Guid.NewGuid().ToString("D");
        var encKey = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);

        var aad = Encoding.UTF8.GetBytes($"{assetId}|{assetId}|{ownerConnectionId}|{Version}");
        var ciphertext = new byte[pfxBytes.Length];
        var tag = new byte[TagLength];

        using (var aes = new AesGcm(encKey, TagLength))
        {
            aes.Encrypt(nonce, pfxBytes, ciphertext, tag, aad);
        }

        var header = BuildHeader(assetId, ownerConnectionId);
        var blob = new byte[header.Length + nonce.Length + ciphertext.Length + tag.Length];
        Buffer.BlockCopy(header, 0, blob, 0, header.Length);
        Buffer.BlockCopy(nonce, 0, blob, header.Length, nonce.Length);
        Buffer.BlockCopy(ciphertext, 0, blob, header.Length + nonce.Length, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, blob, header.Length + nonce.Length + ciphertext.Length, tag.Length);

        var tempPath = Path.Combine(CertificatesDirectory, $"cert-{assetId}.bin.tmp");
        var finalPath = Path.Combine(CertificatesDirectory, $"cert-{assetId}.bin");

        try
        {
            await File.WriteAllBytesAsync(tempPath, blob);
            RestrictFilePermissions(tempPath);

            var envelopeJson = System.Text.Json.JsonSerializer.Serialize(new { v = 1, k = Convert.ToBase64String(encKey), p = internalPassword });
            await _envelopeKeyStore.SetAsync($"cert-env-{assetId}", envelopeJson);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Certificate staging failed after crypto (os={OS}): {ExceptionType}: {Message}. " +
                "Thrown while persisting blob or envelope key (envelope store type={EnvelopeStore}).",
                System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                ex.GetType().FullName, ex.Message, _envelopeKeyStore.GetType().FullName);
            TryDelete(tempPath);
            try { await _envelopeKeyStore.RemoveAsync($"cert-env-{assetId}"); } catch { }
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
            try { await _envelopeKeyStore.RemoveAsync($"cert-env-{assetId}"); } catch { }
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
        if (blob.Length < 73 + NonceLength + TagLength) return null;

        string headerAssetId;
        string headerOwner;
        byte headerVersion;
        try
        {
            headerAssetId = Encoding.ASCII.GetString(blob, 0, 36);
            headerOwner = Encoding.ASCII.GetString(blob, 36, 36);
            headerVersion = blob[72];
        }
        catch { return null; }

        if (!Guid.TryParse(headerAssetId, out var parsedAssetId) || parsedAssetId.ToString("D") != assetId)
            return null;
        if (!Guid.TryParse(headerOwner, out var parsedOwner) || parsedOwner != ownerConnectionId)
            return null;

        var nonce = blob[73..(73 + NonceLength)];
        var ciphertext = blob[(73 + NonceLength)..^TagLength];
        var tag = blob[^TagLength..];

        string? envelopeJson;
        try
        {
            envelopeJson = await _envelopeKeyStore.GetAsync($"cert-env-{assetId}");
        }
        catch { return null; }
        if (envelopeJson is null) return null;

        byte[] encKey;
        string intPwd;
        try
        {
            var envelope = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(envelopeJson);
            var keyB64 = envelope.GetProperty("k").GetString()
                ?? throw new InvalidOperationException("Missing 'k' property");
            encKey = Convert.FromBase64String(keyB64);
            intPwd = envelope.GetProperty("p").GetString()
                ?? throw new InvalidOperationException("Missing 'p' property");
        }
        catch { return null; }

        var aad = Encoding.UTF8.GetBytes($"{assetId}|{headerAssetId}|{headerOwner}|{headerVersion}");
        var pfxBytes = new byte[ciphertext.Length];

        try
        {
            using var aes = new AesGcm(encKey, TagLength);
            aes.Decrypt(nonce, ciphertext, tag, pfxBytes, aad);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or FormatException)
        {
            return null;
        }

        try
        {
            // Windows Schannel cannot use an EphemeralKeySet (in-memory CNG) private key for
            // TLS client authentication — AcquireClientCredentials fails with
            // SEC_E_UNKNOWN_CREDENTIALS (0x8009030D). Load into a real (user) key container on
            // Windows; without PersistKeySet the key is deleted when the cert is disposed, so no
            // key files are left behind. OpenSSL (Linux/macOS) accepts the ephemeral key and
            // keeps it off disk, so keep EphemeralKeySet there.
            var loadFlags = OperatingSystem.IsWindows()
                ? X509KeyStorageFlags.DefaultKeySet
                : X509KeyStorageFlags.EphemeralKeySet;
            var cert = X509CertificateLoader.LoadPkcs12(pfxBytes, intPwd, loadFlags);
            if (cert is null || !cert.HasPrivateKey) { cert?.Dispose(); return null; }
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
        if (blob.Length < 73 + NonceLength + TagLength) return;

        var headerAssetId = Encoding.ASCII.GetString(blob, 0, 36);
        var headerOwner = Encoding.ASCII.GetString(blob, 36, 36);
        var headerVersion = blob[72];

        if (!Guid.TryParse(headerAssetId, out var parsed) || parsed.ToString("D") != assetId) return;
        if (!Guid.TryParse(headerOwner, out var parsedOwner) || parsedOwner != ownerConnectionId) return;

        var nonce = blob[73..(73 + NonceLength)];
        var ciphertext = blob[(73 + NonceLength)..^TagLength];
        var tag = blob[^TagLength..];

        string? envelopeJson;
        try
        {
            envelopeJson = await _envelopeKeyStore.GetAsync($"cert-env-{assetId}");
        }
        catch
        {
            _logger.LogWarning("Cannot read envelope for {AssetId}; skipping delete", assetId);
            return;
        }
        if (envelopeJson is null) return;

        byte[] encKey;
        try
        {
            var envelope = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(envelopeJson);
            encKey = Convert.FromBase64String(envelope.GetProperty("k").GetString()!);
        }
        catch
        {
            _logger.LogWarning("Malformed envelope for {AssetId}; skipping delete", assetId);
            return;
        }

        var aad = Encoding.UTF8.GetBytes($"{assetId}|{headerAssetId}|{headerOwner}|{headerVersion}");
        try
        {
            using var aes = new AesGcm(encKey, TagLength);
            var verifyBuf = new byte[ciphertext.Length];
            aes.Decrypt(nonce, ciphertext, tag, verifyBuf, aad);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or FormatException)
        {
            _logger.LogWarning("Tampered blob {AssetId}; not deleting", assetId);
            return;
        }

        if (TryDelete(path))
        {
            try { await _envelopeKeyStore.RemoveAsync($"cert-env-{assetId}"); } catch { }
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
            if (name.EndsWith(".tmp") || name.EndsWith(".quarantine") || name.EndsWith(".cleanup-retry"))
                continue;

            try
            {
                var blob = await File.ReadAllBytesAsync(file);
                if (blob.Length < 73 + NonceLength + TagLength) continue;

                var headerAssetId = Encoding.ASCII.GetString(blob, 0, 36);
                var headerOwner = Encoding.ASCII.GetString(blob, 36, 36);
                var headerVersion = blob[72];

                if (!Guid.TryParse(headerAssetId, out var aid) || !Guid.TryParse(headerOwner, out var oid))
                    continue;

                var fileNameAssetId = name["cert-".Length..^".bin".Length];
                if (fileNameAssetId != headerAssetId) continue;

                var nonce = blob[73..(73 + NonceLength)];
                var ciphertext = blob[(73 + NonceLength)..^TagLength];
                var tag = blob[^TagLength..];

                var envelopeJson = await _envelopeKeyStore.GetAsync($"cert-env-{aid:D}");
                if (envelopeJson is null) continue;

                var envelope = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(envelopeJson);
                var encKey = Convert.FromBase64String(envelope.GetProperty("k").GetString()!);
                var aad = Encoding.UTF8.GetBytes($"{aid:D}|{headerAssetId}|{headerOwner}|{headerVersion}");

                try
                {
                    using var aes = new AesGcm(encKey, TagLength);
                    aes.Decrypt(nonce, ciphertext, tag, new byte[ciphertext.Length], aad);
                }
                catch (Exception ex) when (ex is CryptographicException or ArgumentException or FormatException) { continue; }

                results.Add((oid, aid.ToString("D")));
            }
            catch { }
        }
        return results;
    }

    private static void ValidateRequest(CertificateImportRequest request)
    {
        if (request.CertificateBytes is not { Length: > 0 })
            throw new CertificateImportException("Certificate bytes are required.");
        if (request.Mode == CertificateInputMode.Pfx && request.Password is null)
            throw new CertificateImportException("PFX password is required (may be empty string).");
        if (request.Mode == CertificateInputMode.Pem && request.PrivateKeyBytes is not { Length: > 0 })
            throw new CertificateImportException("PEM private key bytes are required.");
    }

    private (byte[] pfxBytes, string internalPassword) ImportPfx(CertificateImportRequest request)
    {
        if (request.SkipCanonicalExport)
        {
            X509Certificate2 cert;
            try
            {
                cert = X509CertificateLoader.LoadPkcs12(
                    request.CertificateBytes, request.Password,
                    X509KeyStorageFlags.EphemeralKeySet);
            }
            catch (CryptographicException ex)
            {
                throw new CertificateImportException("The PFX password is incorrect or the file is corrupt.", ex);
            }

            if (!cert.HasPrivateKey)
            {
                cert.Dispose();
                throw new CertificateImportException("The certificate does not contain a private key.");
            }

            cert.Dispose();
            return (request.CertificateBytes, request.Password!);
        }

        X509Certificate2 loadedCert;
        try
        {
            loadedCert = X509CertificateLoader.LoadPkcs12(
                request.CertificateBytes, request.Password,
                X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
        }
        catch (PlatformNotSupportedException)
        {
            try
            {
                loadedCert = X509CertificateLoader.LoadPkcs12(
                    request.CertificateBytes, request.Password,
                    X509KeyStorageFlags.DefaultKeySet | X509KeyStorageFlags.Exportable);
            }
            catch (PlatformNotSupportedException)
            {
                throw new CertificateImportException("Platform does not support certificate key export.");
            }
            catch (CryptographicException ex)
            {
                throw new CertificateImportException("The PFX password is incorrect or the file is corrupt.", ex);
            }
        }
        catch (CryptographicException ex)
        {
            throw new CertificateImportException("The PFX password is incorrect or the file is corrupt.", ex);
        }

        if (!loadedCert.HasPrivateKey)
        {
            loadedCert.Dispose();
            throw new CertificateImportException("The certificate does not contain a private key.");
        }

        return ExportCanonicalPfx(loadedCert);
    }

    private (byte[] pfxBytes, string internalPassword) ImportPem(CertificateImportRequest request)
    {
        var certPem = Encoding.UTF8.GetString(request.CertificateBytes);
        var keyPem = Encoding.UTF8.GetString(request.PrivateKeyBytes!);

        using var cert = X509Certificate2.CreateFromPem(certPem);

        var isEncrypted = keyPem.Contains("BEGIN ENCRYPTED PRIVATE KEY");
        if (isEncrypted && string.IsNullOrEmpty(request.Password))
            throw new CertificateImportException("The private key is encrypted. Enter the key password.");

        X509Certificate2 validatedCert;
        AsymmetricAlgorithm? key = null;
        try
        {
            var algOid = cert.PublicKey.Oid.Value;
            if (algOid == OidRsa)
            {
                var rsa = RSA.Create();
                key = rsa;
                if (isEncrypted) rsa.ImportFromEncryptedPem(keyPem, request.Password);
                else rsa.ImportFromPem(keyPem);
                validatedCert = cert.CopyWithPrivateKey(rsa);
            }
            else if (algOid == OidEcc)
            {
                var ecdsa = ECDsa.Create();
                key = ecdsa;
                if (isEncrypted) ecdsa.ImportFromEncryptedPem(keyPem, request.Password);
                else ecdsa.ImportFromPem(keyPem);
                validatedCert = cert.CopyWithPrivateKey(ecdsa);
            }
            else
            {
                throw new CertificateImportException(
                    $"Unsupported certificate public-key algorithm (OID {algOid}). Only RSA and ECDSA are supported.");
            }
        }
        catch (CertificateImportException) { key?.Dispose(); throw; }
        catch (CryptographicException ex)
        {
            key?.Dispose();
            if (isEncrypted)
                throw new CertificateImportException("The key password is incorrect.", ex);
            throw new CertificateImportException("The private key is invalid or does not match the certificate.", ex);
        }
        catch { key?.Dispose(); throw; }
        finally
        {
            key?.Dispose();
        }

        if (!validatedCert.HasPrivateKey)
        {
            validatedCert.Dispose();
            throw new CertificateImportException("The certificate does not contain a private key.");
        }

        return ExportCanonicalPfx(validatedCert);
    }

    private (byte[] pfxBytes, string internalPassword) ExportCanonicalPfx(X509Certificate2 validatedCert)
    {
        var internalPassword = Guid.NewGuid().ToString("D");
        byte[] pfxBytes;
        try
        {
            try
            {
                pfxBytes = validatedCert.Export(X509ContentType.Pfx, internalPassword);
            }
            catch (CryptographicException ex)
            {
                throw new CertificateImportException("Failed to export certificate to PKCS#12 format.", ex);
            }
        }
        finally
        {
            validatedCert.Dispose();
        }
        return (pfxBytes, internalPassword);
    }

    private static byte[] BuildHeader(string assetId, Guid ownerConnectionId)
    {
        var header = new byte[73];
        Encoding.ASCII.GetBytes(assetId, 0, 36, header, 0);
        Encoding.ASCII.GetBytes(ownerConnectionId.ToString("D"), 0, 36, header, 36);
        header[72] = Version;
        return header;
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
}
