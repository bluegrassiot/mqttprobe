using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
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
            var loadFlags = GetClientCertificateLoadFlags(
                OperatingSystem.IsWindows(),
                OperatingSystem.IsMacOS());
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
        var certText = DecodePemText(request.CertificateBytes);
        var keyText = DecodePemText(request.PrivateKeyBytes!);

        if (LooksLikePrivateKeyPem(certText) && !LooksLikeCertificatePem(certText))
            throw new CertificateImportException(
                "The certificate file looks like a private key. Select the certificate (.crt/.pem) for the certificate field and the private key (.key/.pem) for the key field.");

        if (LooksLikeCertificatePem(keyText) && !LooksLikePrivateKeyPem(keyText))
            throw new CertificateImportException(
                "The private key file looks like a certificate. Select the private key (.key/.pem) for the key field.");

        using var cert = LoadCertificateFromPemOrDer(request.CertificateBytes, certText);

        var isEncrypted = keyText.Contains("BEGIN ENCRYPTED PRIVATE KEY", StringComparison.OrdinalIgnoreCase);
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
                if (isEncrypted) rsa.ImportFromEncryptedPem(keyText, request.Password);
                else rsa.ImportFromPem(keyText);
                validatedCert = cert.CopyWithPrivateKey(rsa);
            }
            else if (algOid == OidEcc)
            {
                var ecdsa = ECDsa.Create();
                key = ecdsa;
                if (isEncrypted) ecdsa.ImportFromEncryptedPem(keyText, request.Password);
                else ecdsa.ImportFromPem(keyText);
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
            if (LooksLikeCertificatePem(keyText))
                throw new CertificateImportException(
                    "The private key file looks like a certificate. Select the private key (.key/.pem) for the key field.", ex);
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

    private static readonly Regex _certPemBlock = new(
        @"-----BEGIN (?<label>[^-]+)-----(?<body>.*?)-----END \k<label>-----",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string DecodePemText(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes).TrimStart('\uFEFF').Trim();
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes).TrimStart('\uFEFF').Trim();

        var utf8 = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF').Trim();
        if (utf8.Contains("-----BEGIN", StringComparison.Ordinal))
            return utf8;

        if (bytes.Length >= 4 && bytes[1] == 0 && bytes[3] == 0 && bytes[0] != 0)
        {
            var utf16 = Encoding.Unicode.GetString(bytes).TrimStart('\uFEFF').Trim();
            if (utf16.Contains("-----BEGIN", StringComparison.Ordinal))
                return utf16;
        }

        return utf8;
    }

    private static bool LooksLikePem(string text) =>
        text.Contains("-----BEGIN", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikePrivateKeyPem(string text) =>
        text.Contains("BEGIN ", StringComparison.OrdinalIgnoreCase)
        && text.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeCertificatePem(string text) =>
        text.Contains("BEGIN CERTIFICATE", StringComparison.OrdinalIgnoreCase)
        || text.Contains("BEGIN TRUSTED CERTIFICATE", StringComparison.OrdinalIgnoreCase)
        || text.Contains("BEGIN X509 CERTIFICATE", StringComparison.OrdinalIgnoreCase);

    private static string? ExtractCertificatePem(string text)
    {
        foreach (Match m in _certPemBlock.Matches(text))
        {
            var label = m.Groups["label"].Value.Trim();
            if (label.Contains("CERTIFICATE", StringComparison.OrdinalIgnoreCase)
                && !label.Contains("REQUEST", StringComparison.OrdinalIgnoreCase))
            {
                return "-----BEGIN CERTIFICATE-----\n"
                    + m.Groups["body"].Value.Trim()
                    + "\n-----END CERTIFICATE-----";
            }
        }
        return null;
    }

    private static bool LooksLikeDerPrivateKey(byte[] rawBytes)
    {
        try { using var rsa = RSA.Create(); rsa.ImportPkcs8PrivateKey(rawBytes, out _); return true; }
        catch { }
        try { using var rsa = RSA.Create(); rsa.ImportRSAPrivateKey(rawBytes, out _); return true; }
        catch { }
        try { using var ecdsa = ECDsa.Create(); ecdsa.ImportPkcs8PrivateKey(rawBytes, out _); return true; }
        catch { }
        return false;
    }

    private static X509Certificate2 LoadCertificateFromPemOrDer(byte[] rawBytes, string certText)
    {
        if (LooksLikePem(certText))
        {
            try
            {
                return X509Certificate2.CreateFromPem(certText);
            }
            catch { }

            var extracted = ExtractCertificatePem(certText);
            if (extracted is not null)
            {
                try
                {
                    return X509Certificate2.CreateFromPem(extracted);
                }
                catch (Exception ex) when (ex is CryptographicException or ArgumentException or FormatException)
                {
                    throw new CertificateImportException(
                        "The certificate PEM could not be parsed. Ensure the file contains a "
                        + "-----BEGIN CERTIFICATE----- block (not only a private key or CA bag).",
                        ex);
                }
            }

            if (LooksLikePrivateKeyPem(certText))
                throw new CertificateImportException(
                    "The certificate file looks like a private key. Select the certificate for the certificate field.");

            throw new CertificateImportException(
                "The certificate file contains PEM data but no CERTIFICATE block was found. "
                + "Expected -----BEGIN CERTIFICATE-----.");
        }

        try
        {
            return X509CertificateLoader.LoadCertificate(rawBytes);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or FormatException)
        {
            if (LooksLikeDerPrivateKey(rawBytes))
                throw new CertificateImportException(
                    "The certificate file looks like a private key (binary). Select the certificate (.crt) for the certificate field and the key for the key field.",
                    ex);

            throw new CertificateImportException(
                "The certificate file is not a valid PEM or DER certificate. "
                + "Expected -----BEGIN CERTIFICATE----- or a binary DER .crt.",
                ex);
        }
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

    internal static X509KeyStorageFlags GetClientCertificateLoadFlags(
        bool isWindows,
        bool isMacOs) =>
        isWindows || isMacOs
            ? X509KeyStorageFlags.DefaultKeySet
            : X509KeyStorageFlags.EphemeralKeySet;
}
