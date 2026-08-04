using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MqttProbe.Models.Mqtt;
using MqttProbe.Services.Security;
using MqttProbe.Tests.Services.Security.TestHelpers;

namespace MqttProbe.Shared.Tests.Services.Security;

[TestFixture]
public class CertificateImportConverterTests
{
    [Test]
    public void Import_MissingCertificateBytes_ThrowsExactMessage()
    {
        var request = new CertificateImportRequest(CertificateInputMode.Pfx, [], null, "password");

        var act = () => CertificateImportConverter.Import(request);

        act.Should().Throw<CertificateImportException>()
            .Which.Message.Should().Be("Certificate bytes are required.");
    }

    [Test]
    public void Import_PfxMissingPassword_ThrowsExactMessage()
    {
        var (pfxBytes, _) = TestCertFactory.CreatePfx();
        var request = new CertificateImportRequest(CertificateInputMode.Pfx, pfxBytes, null, null);

        var act = () => CertificateImportConverter.Import(request);

        act.Should().Throw<CertificateImportException>()
            .Which.Message.Should().Be("PFX password is required (may be empty string).");
    }

    [Test]
    public void Import_PemMissingPrivateKeyBytes_ThrowsExactMessage()
    {
        var (certPem, _) = TestCertFactory.CreatePemRsa();
        var request = new CertificateImportRequest(CertificateInputMode.Pem, certPem, null, null);

        var act = () => CertificateImportConverter.Import(request);

        act.Should().Throw<CertificateImportException>()
            .Which.Message.Should().Be("PEM private key bytes are required.");
    }

    [Test]
    public void Import_PfxWithCorrectPassword_ReturnsCanonicalPfxAndGeneratedPassword()
    {
        var (pfxBytes, password) = TestCertFactory.CreatePfx();
        var request = new CertificateImportRequest(CertificateInputMode.Pfx, pfxBytes, null, password);

        var result = CertificateImportConverter.Import(request);

        result.PfxBytes.Should().NotBeSameAs(pfxBytes);
        result.InternalPassword.Should().NotBeNullOrWhiteSpace();
        using var cert = X509CertificateLoader.LoadPkcs12(
            result.PfxBytes, result.InternalPassword, X509KeyStorageFlags.EphemeralKeySet);
        cert.HasPrivateKey.Should().BeTrue();
    }

    [Test]
    public void Import_PfxWrongPassword_ThrowsExactMessage()
    {
        var (pfxBytes, _) = TestCertFactory.CreatePfx();
        var request = new CertificateImportRequest(CertificateInputMode.Pfx, pfxBytes, null, "wrong");

        var act = () => CertificateImportConverter.Import(request);

        act.Should().Throw<CertificateImportException>()
            .Which.Message.Should().Be("The PFX password is incorrect or the file is corrupt.");
    }

    [Test]
    public void Import_PfxEmptyPassword_Succeeds()
    {
        var (pfxBytes, password) = TestCertFactory.CreateEmptyPasswordPfx();
        var request = new CertificateImportRequest(CertificateInputMode.Pfx, pfxBytes, null, password);

        var result = CertificateImportConverter.Import(request);

        result.PfxBytes.Should().NotBeEmpty();
        result.InternalPassword.Should().NotBeNullOrWhiteSpace();
    }

    [Test]
    public void Import_PfxWithoutPrivateKey_ThrowsExactMessage()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=NoKey", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.Now, DateTimeOffset.Now.AddHours(1));
        using var publicCert = X509CertificateLoader.LoadCertificate(cert.Export(X509ContentType.Cert));
        var request = new CertificateImportRequest(
            CertificateInputMode.Pfx, publicCert.Export(X509ContentType.Pfx, ""), null, "");

        var act = () => CertificateImportConverter.Import(request);

        act.Should().Throw<CertificateImportException>()
            .Which.Message.Should().Be("The certificate does not contain a private key.");
    }

    [Test]
    public void Import_PfxSkipCanonicalExport_ReturnsOriginalBytesAndPassword()
    {
        var (pfxBytes, password) = TestCertFactory.CreatePfx();
        var request = new CertificateImportRequest(
            CertificateInputMode.Pfx, pfxBytes, null, password, SkipCanonicalExport: true);

        var result = CertificateImportConverter.Import(request);

        result.PfxBytes.Should().BeSameAs(pfxBytes);
        result.InternalPassword.Should().Be(password);
    }

    [Test]
    public void Import_PfxSkipCanonicalExportWrongPassword_ThrowsExactMessage()
    {
        var (pfxBytes, _) = TestCertFactory.CreatePfx();
        var request = new CertificateImportRequest(
            CertificateInputMode.Pfx, pfxBytes, null, "wrong", SkipCanonicalExport: true);

        var act = () => CertificateImportConverter.Import(request);

        act.Should().Throw<CertificateImportException>()
            .Which.Message.Should().Be("The PFX password is incorrect or the file is corrupt.");
    }

    [Test]
    public void Import_PfxSkipCanonicalExportWithoutPrivateKey_ThrowsExactMessage()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=NoKey", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.Now, DateTimeOffset.Now.AddHours(1));
        using var publicCert = X509CertificateLoader.LoadCertificate(cert.Export(X509ContentType.Cert));
        var request = new CertificateImportRequest(
            CertificateInputMode.Pfx, publicCert.Export(X509ContentType.Pfx, ""), null, "",
            SkipCanonicalExport: true);

        var act = () => CertificateImportConverter.Import(request);

        act.Should().Throw<CertificateImportException>()
            .Which.Message.Should().Be("The certificate does not contain a private key.");
    }

    [Test]
    public void Import_PemRsaUnencrypted_Succeeds()
    {
        var (certPem, keyPem) = TestCertFactory.CreatePemRsa();

        var result = CertificateImportConverter.Import(
            new CertificateImportRequest(CertificateInputMode.Pem, certPem, keyPem, null));

        result.PfxBytes.Should().NotBeEmpty();
        result.InternalPassword.Should().NotBeNullOrWhiteSpace();
    }

    [Test]
    public void Import_PemEcdsaUnencrypted_Succeeds()
    {
        var (certPem, keyPem) = TestCertFactory.CreatePemEcdsa();

        var result = CertificateImportConverter.Import(
            new CertificateImportRequest(CertificateInputMode.Pem, certPem, keyPem, null));

        result.PfxBytes.Should().NotBeEmpty();
    }

    [Test]
    public void Import_PemEncryptedRsaWithCorrectPassword_Succeeds()
    {
        var (certPem, keyPem, password) = TestCertFactory.CreatePemEncryptedRsa();

        var result = CertificateImportConverter.Import(
            new CertificateImportRequest(CertificateInputMode.Pem, certPem, keyPem, password));

        result.PfxBytes.Should().NotBeEmpty();
    }

    [Test]
    public void Import_PemEncryptedRsaWithWrongPassword_ThrowsExactMessage()
    {
        var (certPem, keyPem, _) = TestCertFactory.CreatePemEncryptedRsa();

        var act = () => CertificateImportConverter.Import(
            new CertificateImportRequest(CertificateInputMode.Pem, certPem, keyPem, "wrong"));

        act.Should().Throw<CertificateImportException>()
            .Which.Message.Should().Be("The key password is incorrect.");
    }

    [Test]
    public void Import_PemEncryptedRsaWithoutPassword_ThrowsExactMessage()
    {
        var (certPem, keyPem, _) = TestCertFactory.CreatePemEncryptedRsa();

        var act = () => CertificateImportConverter.Import(
            new CertificateImportRequest(CertificateInputMode.Pem, certPem, keyPem, null));

        act.Should().Throw<CertificateImportException>()
            .Which.Message.Should().Be("The private key is encrypted. Enter the key password.");
    }

    [Test]
    public void Import_PemEncryptedEcdsaWithCorrectPassword_Succeeds()
    {
        var (certPem, keyPem, password) = TestCertFactory.CreatePemEncryptedEcdsa();

        var result = CertificateImportConverter.Import(
            new CertificateImportRequest(CertificateInputMode.Pem, certPem, keyPem, password));

        result.PfxBytes.Should().NotBeEmpty();
    }

    [Test]
    public void Import_PemEncryptedEcdsaWithWrongPassword_ThrowsExactMessage()
    {
        var (certPem, keyPem, _) = TestCertFactory.CreatePemEncryptedEcdsa();

        var act = () => CertificateImportConverter.Import(
            new CertificateImportRequest(CertificateInputMode.Pem, certPem, keyPem, "wrong"));

        act.Should().Throw<CertificateImportException>()
            .Which.Message.Should().Be("The key password is incorrect.");
    }

    [Test]
    public void Import_PemEncryptedEcdsaWithoutPassword_ThrowsExactMessage()
    {
        var (certPem, keyPem, _) = TestCertFactory.CreatePemEncryptedEcdsa();

        var act = () => CertificateImportConverter.Import(
            new CertificateImportRequest(CertificateInputMode.Pem, certPem, keyPem, null));

        act.Should().Throw<CertificateImportException>()
            .Which.Message.Should().Be("The private key is encrypted. Enter the key password.");
    }

    [Test]
    public void Import_PemMismatchedKeys_ThrowsExactMessage()
    {
        var (certPem, _) = TestCertFactory.CreatePemRsa();
        var (_, keyPem) = TestCertFactory.CreatePemEcdsa();

        var act = () => CertificateImportConverter.Import(
            new CertificateImportRequest(CertificateInputMode.Pem, certPem, keyPem, null));

        act.Should().Throw<CertificateImportException>()
            .Which.Message.Should().Be("The private key is invalid or does not match the certificate.");
    }

    [Test]
    public void Import_DerCertificate_Succeeds()
    {
        using var cert = TestCertFactory.CreateRsaCert();
        var certDer = cert.Export(X509ContentType.Cert);
        var keyPem = Encoding.UTF8.GetBytes(cert.GetRSAPrivateKey()!.ExportPkcs8PrivateKeyPem());

        var result = CertificateImportConverter.Import(
            new CertificateImportRequest(CertificateInputMode.Pem, certDer, keyPem, null));

        result.PfxBytes.Should().NotBeEmpty();
    }

    [Test]
    public void Import_Utf8BomCertificate_Succeeds()
    {
        var (certPem, keyPem) = TestCertFactory.CreatePemRsa();
        var bomCertPem = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(certPem).ToArray();

        var result = CertificateImportConverter.Import(
            new CertificateImportRequest(CertificateInputMode.Pem, bomCertPem, keyPem, null));

        result.PfxBytes.Should().NotBeEmpty();
    }

    [Test]
    public void Import_Utf16LeCertificate_Succeeds()
    {
        var (certPem, keyPem) = TestCertFactory.CreatePemRsa();
        var certText = Encoding.UTF8.GetString(certPem);
        var utf16CertPem = Encoding.Unicode.GetPreamble()
            .Concat(Encoding.Unicode.GetBytes(certText)).ToArray();

        var result = CertificateImportConverter.Import(
            new CertificateImportRequest(CertificateInputMode.Pem, utf16CertPem, keyPem, null));

        result.PfxBytes.Should().NotBeEmpty();
    }

    [Test]
    public void Import_TrustedCertificateLabel_Succeeds()
    {
        var (certPem, keyPem) = TestCertFactory.CreatePemRsa();
        var trustedCertPem = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(certPem)
                .Replace("BEGIN CERTIFICATE", "BEGIN TRUSTED CERTIFICATE")
                .Replace("END CERTIFICATE", "END TRUSTED CERTIFICATE"));

        var result = CertificateImportConverter.Import(
            new CertificateImportRequest(CertificateInputMode.Pem, trustedCertPem, keyPem, null));

        result.PfxBytes.Should().NotBeEmpty();
    }

    [Test]
    public void Import_CombinedPemInCertificateSlot_Succeeds()
    {
        var (certPem, keyPem) = TestCertFactory.CreatePemRsa();

        var result = CertificateImportConverter.Import(
            new CertificateImportRequest(CertificateInputMode.Pem, certPem.Concat(keyPem).ToArray(), keyPem, null));

        result.PfxBytes.Should().NotBeEmpty();
    }

    [Test]
    public void Import_PrivateKeyInCertificateSlot_ThrowsExactMessage()
    {
        var (certPem, keyPem) = TestCertFactory.CreatePemRsa();

        var act = () => CertificateImportConverter.Import(
            new CertificateImportRequest(CertificateInputMode.Pem, keyPem, certPem, null));

        act.Should().Throw<CertificateImportException>().Which.Message.Should().Be(
            "The certificate file looks like a private key. Select the certificate (.crt/.pem) for the certificate field and the private key (.key/.pem) for the key field.");
    }

    [Test]
    public void Import_CertificateInPrivateKeySlot_ThrowsExactMessage()
    {
        var (certPem, _) = TestCertFactory.CreatePemRsa();

        var act = () => CertificateImportConverter.Import(
            new CertificateImportRequest(CertificateInputMode.Pem, certPem, certPem, null));

        act.Should().Throw<CertificateImportException>().Which.Message.Should().Be(
            "The private key file looks like a certificate. Select the private key (.key/.pem) for the key field.");
    }

    [Test]
    public void Import_DerPrivateKeyInCertificateSlot_ThrowsExactMessage()
    {
        using var cert = TestCertFactory.CreateRsaCert();
        var derKey = cert.GetRSAPrivateKey()!.ExportPkcs8PrivateKey();
        var keyPem = Encoding.UTF8.GetBytes(cert.GetRSAPrivateKey()!.ExportPkcs8PrivateKeyPem());

        var act = () => CertificateImportConverter.Import(
            new CertificateImportRequest(CertificateInputMode.Pem, derKey, keyPem, null));

        act.Should().Throw<CertificateImportException>().Which.Message.Should().Be(
            "The certificate file looks like a private key (binary). Select the certificate (.crt) for the certificate field and the key for the key field.");
    }

    [Test]
    public void Import_MalformedPem_ThrowsExactMessage()
    {
        var (_, keyPem) = TestCertFactory.CreatePemRsa();
        var malformed = Encoding.UTF8.GetBytes("-----BEGIN GARBAGE-----\nnot-a-certificate\n-----END GARBAGE-----");

        var act = () => CertificateImportConverter.Import(
            new CertificateImportRequest(CertificateInputMode.Pem, malformed, keyPem, null));

        act.Should().Throw<CertificateImportException>().Which.Message.Should().Be(
            "The certificate file contains PEM data but no CERTIFICATE block was found. Expected -----BEGIN CERTIFICATE-----.");
    }
}
