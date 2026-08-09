using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MqttProbe.TestInfrastructure.Security;

public static class TestCertFactory
{
    public static X509Certificate2 CreateRsaCert()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return req.CreateSelfSigned(DateTimeOffset.Now, DateTimeOffset.Now.AddHours(1));
    }

    public static X509Certificate2 CreateEcdsaCert()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256); // DevSkim: ignore DS440100 - throwaway self-signed test cert
        var req = new CertificateRequest("CN=TestEC", ecdsa, HashAlgorithmName.SHA256);
        return req.CreateSelfSigned(DateTimeOffset.Now, DateTimeOffset.Now.AddHours(1));
    }

    public static (byte[] pfx, string password) CreatePfx()
    {
        using var cert = CreateRsaCert();
        var password = "testpass";
        var pfxBytes = cert.Export(X509ContentType.Pfx, password);
        return (pfxBytes, password);
    }

    public static (byte[] pfx, string password) CreateEmptyPasswordPfx()
    {
        using var cert = CreateRsaCert();
        var pfxBytes = cert.Export(X509ContentType.Pfx, "");
        return (pfxBytes, "");
    }

    public static (byte[] certPem, byte[] keyPem) CreatePemRsa()
    {
        using var cert = CreateRsaCert();
        var certPem = System.Text.Encoding.UTF8.GetBytes(cert.ExportCertificatePem());
        var keyPem = System.Text.Encoding.UTF8.GetBytes(cert.GetRSAPrivateKey()!.ExportPkcs8PrivateKeyPem());
        return (certPem, keyPem);
    }

    public static (byte[] certPem, byte[] keyPem) CreatePemEcdsa()
    {
        using var cert = CreateEcdsaCert();
        var certPem = System.Text.Encoding.UTF8.GetBytes(cert.ExportCertificatePem());
        var keyPem = System.Text.Encoding.UTF8.GetBytes(cert.GetECDsaPrivateKey()!.ExportPkcs8PrivateKeyPem());
        return (certPem, keyPem);
    }

    public static (byte[] certPem, byte[] encKeyPem, string password) CreatePemEncryptedRsa()
    {
        using var cert = CreateRsaCert();
        var certPem = System.Text.Encoding.UTF8.GetBytes(cert.ExportCertificatePem());
        var key = cert.GetRSAPrivateKey()!;
        var password = "keypass";
        var encKeyPem = System.Text.Encoding.UTF8.GetBytes(
            key.ExportEncryptedPkcs8PrivateKeyPem(password,
                new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000)));
        return (certPem, encKeyPem, password);
    }

    public static (byte[] certPem, byte[] encKeyPem, string password) CreatePemEncryptedEcdsa()
    {
        using var cert = CreateEcdsaCert();
        var certPem = System.Text.Encoding.UTF8.GetBytes(cert.ExportCertificatePem());
        var key = cert.GetECDsaPrivateKey()!;
        var password = "keypass";
        var encKeyPem = System.Text.Encoding.UTF8.GetBytes(
            key.ExportEncryptedPkcs8PrivateKeyPem(password,
                new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000)));
        return (certPem, encKeyPem, password);
    }

    public static byte[] CreatePfxBytes(X509Certificate2 cert, string password)
        => cert.Export(X509ContentType.Pfx, password);
}
