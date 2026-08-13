using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using MqttProbe.Core.Models.Mqtt;

namespace MqttProbe.Core.Services.Security;

internal static class CertificateImportConverter
{
    private const string OidRsa = "1.2.840.113549.1.1.1";
    private const string OidEcc = "1.2.840.10045.2.1";

    public static (byte[] PfxBytes, string InternalPassword) Import(CertificateImportRequest request)
    {
        ValidateRequest(request);

        return request.Mode == CertificateInputMode.Pfx
            ? ImportPfx(request)
            : ImportPem(request);
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

    private static (byte[] PfxBytes, string InternalPassword) ImportPfx(CertificateImportRequest request)
    {
        if (request.SkipCanonicalExport)
            return ImportPfxAsIs(request);

        var loadedCert = LoadExportablePfx(request);

        if (!loadedCert.HasPrivateKey)
        {
            loadedCert.Dispose();
            throw new CertificateImportException("The certificate does not contain a private key.");
        }

        return ExportCanonicalPfx(loadedCert);
    }

    // Catches the common mix-up of passing the certificate and key files the wrong way round,
    // which otherwise surfaces much later as an opaque crypto error.
    private static void ValidatePemFilesNotSwapped(string certText, string keyText)
    {
        if (LooksLikePrivateKeyPem(certText) && !LooksLikeCertificatePem(certText))
            throw new CertificateImportException(
                "The certificate file looks like a private key. Select the certificate (.crt/.pem) for the certificate field and the private key (.key/.pem) for the key field.");

        if (LooksLikeCertificatePem(keyText) && !LooksLikePrivateKeyPem(keyText))
            throw new CertificateImportException(
                "The private key file looks like a certificate. Select the private key (.key/.pem) for the key field.");
    }

    // The algorithm object is only needed long enough for CopyWithPrivateKey to take its own
    // copy, so it is disposed on every path — including the finally, which is why the catch
    // blocks can dispose too without harm.
    private static X509Certificate2 AttachPrivateKey(
        X509Certificate2 cert, string keyText, bool isEncrypted, string? password)
    {
        X509Certificate2 validatedCert;
        AsymmetricAlgorithm? key = null;
        try
        {
            var algOid = cert.PublicKey.Oid.Value;
            if (algOid == OidRsa)
            {
                var rsa = RSA.Create();
                key = rsa;
                if (isEncrypted) rsa.ImportFromEncryptedPem(keyText, password);
                else rsa.ImportFromPem(keyText);
                validatedCert = cert.CopyWithPrivateKey(rsa);
            }
            else if (algOid == OidEcc)
            {
                var ecdsa = ECDsa.Create();
                key = ecdsa;
                if (isEncrypted) ecdsa.ImportFromEncryptedPem(keyText, password);
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

        return validatedCert;
    }

    // The caller already has a usable PFX, so this only proves the password works and a
    // private key is present, then hands the original bytes back untouched.
    private static (byte[] PfxBytes, string InternalPassword) ImportPfxAsIs(CertificateImportRequest request)
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

    // EphemeralKeySet is not supported everywhere, so fall back to DefaultKeySet before
    // giving up. Both paths report a bad password identically.
    private static X509Certificate2 LoadExportablePfx(CertificateImportRequest request)
    {
        try
        {
            return X509CertificateLoader.LoadPkcs12(
                request.CertificateBytes, request.Password,
                X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
        }
        catch (PlatformNotSupportedException)
        {
            try
            {
                return X509CertificateLoader.LoadPkcs12(
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
    }

    private static (byte[] PfxBytes, string InternalPassword) ImportPem(CertificateImportRequest request)
    {
        var certText = DecodePemText(request.CertificateBytes);
        var keyText = DecodePemText(request.PrivateKeyBytes!);

        ValidatePemFilesNotSwapped(certText, keyText);

        using var cert = LoadCertificateFromPemOrDer(request.CertificateBytes, certText);

        var isEncrypted = keyText.Contains("BEGIN ENCRYPTED PRIVATE KEY", StringComparison.OrdinalIgnoreCase);
        if (isEncrypted && string.IsNullOrEmpty(request.Password))
            throw new CertificateImportException("The private key is encrypted. Enter the key password.");

        var validatedCert = AttachPrivateKey(cert, keyText, isEncrypted, request.Password);

        if (!validatedCert.HasPrivateKey)
        {
            validatedCert.Dispose();
            throw new CertificateImportException("The certificate does not contain a private key.");
        }

        return ExportCanonicalPfx(validatedCert);
    }

    private static readonly Regex _certPemBlock = new(
        @"-----BEGIN (?<label>[^-]+)-----(?<body>.*?)-----END \k<label>-----",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

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
        var match = _certPemBlock.Matches(text).FirstOrDefault(m =>
        {
            var label = m.Groups["label"].Value.Trim();
            return label.Contains("CERTIFICATE", StringComparison.OrdinalIgnoreCase)
                && !label.Contains("REQUEST", StringComparison.OrdinalIgnoreCase);
        });

        return match is null
            ? null
            : "-----BEGIN CERTIFICATE-----\n"
                + match.Groups["body"].Value.Trim()
                + "\n-----END CERTIFICATE-----";
    }

    private static bool LooksLikeDerPrivateKey(byte[] rawBytes)
    {
        try
        {
            using var rsa = RSA.Create(); rsa.ImportPkcs8PrivateKey(rawBytes, out _); return true;
        }
        catch { /* not PKCS#8; try the next format */ }

        try
        {
            using var rsa = RSA.Create(); rsa.ImportRSAPrivateKey(rawBytes, out _); return true;
        }
        catch { /* not PKCS#1; try the next format */ }

        try
        {
            using var ecdsa = ECDsa.Create(); ecdsa.ImportPkcs8PrivateKey(rawBytes, out _); return true;
        }
        catch { /* not an EC key either; no formats left */ }
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
            catch { /* whole-text parse failed; retry below with just the certificate block */ }

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

    private static (byte[] PfxBytes, string InternalPassword) ExportCanonicalPfx(X509Certificate2 validatedCert)
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
}
