using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using NUnit.Framework;

namespace MqttProbe.TestInfrastructure.Fixtures;

/// <summary>
/// Shared Testcontainers Mosquitto fixture with mTLS.
/// Generates CA-signed server and client certs with a shared validity window.
/// Starts a Mosquitto container requiring client certificates on port 8883.
/// </summary>
public sealed class MtlsBrokerFixture : IAsyncDisposable
{
    // Immutable image digest — do not replace with a floating tag.
    private const string MosquittoImage =
        "eclipse-mosquitto@sha256:94f5a3d7deafa59fa3440d227ddad558f59d293c612138de841eec61bfa4d353";

    private IContainer? _container;
    private X509Certificate2? _caCertWithKey;
    private X509Certificate2? _caCert;
    private X509Certificate2? _serverCert;
    private X509Certificate2? _clientCert;

    private MtlsBrokerFixture()
    {
    }

    public int Port { get; private set; }
    public X509Certificate2 CaCert => _caCert ?? throw new InvalidOperationException("Fixture not initialized.");
    public X509Certificate2 ClientCert => _clientCert ?? throw new InvalidOperationException("Fixture not initialized.");
    public byte[] ClientPfxBytes => ClientCert.Export(X509ContentType.Pfx, ClientPfxPassword);
    public string ClientPfxPassword => "test";

    public static async Task<MtlsBrokerFixture> StartAsync()
    {
        var fixture = new MtlsBrokerFixture();
        try
        {
            await fixture.SetupAsync();
            return fixture;
        }
        catch (Exception ex) when (IsDockerUnavailable(ex))
        {
            await fixture.DisposeAsync();
            Assert.Ignore("Docker is not available on this runner — integration tests skipped.");
            throw; // unreachable, keeps compiler happy
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
        _caCert?.Dispose();
        _caCertWithKey?.Dispose();
        _serverCert?.Dispose();
        _clientCert?.Dispose();
    }

    private async Task SetupAsync()
    {
        var notBefore = DateTimeOffset.Now;
        var notAfter = notBefore.AddHours(1);

        // CA
        using var caRsa = RSA.Create(2048);
        var caReq = new CertificateRequest("CN=TestCA", caRsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caReq.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        caReq.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(caReq.PublicKey, false));
        _caCertWithKey = caReq.CreateSelfSigned(notBefore, notAfter);
        _caCert = X509CertificateLoader.LoadCertificate(_caCertWithKey.Export(X509ContentType.Cert));

        // Server cert
        using var serverRsa = RSA.Create(2048);
        var serverReq = new CertificateRequest("CN=localhost", serverRsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        serverReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        serverReq.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        serverReq.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        sanBuilder.AddDnsName("localhost");
        serverReq.CertificateExtensions.Add(sanBuilder.Build());
        var serverSerial = RandomNumberGenerator.GetBytes(16);
        var serverCertSigned = serverReq.Create(_caCertWithKey, notBefore, notAfter, serverSerial);
        _serverCert = serverCertSigned.CopyWithPrivateKey(serverRsa);

        // Client cert
        using var clientRsa = RSA.Create(2048);
        var clientReq = new CertificateRequest("CN=TestClient", clientRsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        clientReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        clientReq.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        clientReq.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.2") }, false));
        var clientSerial = RandomNumberGenerator.GetBytes(16);
        var clientCertSigned = clientReq.Create(_caCertWithKey, notBefore, notAfter, clientSerial);
        _clientCert = clientCertSigned.CopyWithPrivateKey(clientRsa);

        // Export PEM bytes in memory — no temp files, no bind mounts.
        var caPem = _caCertWithKey.ExportCertificatePem();
        var serverCertPem = _serverCert.ExportCertificatePem();
        var serverKeyPem = _serverCert.GetRSAPrivateKey()!.ExportPkcs8PrivateKeyPem();

        var config = """
            listener 8883
            protocol mqtt
            allow_anonymous true
            require_certificate true
            cafile /mosquitto/config/ca.crt
            certfile /mosquitto/config/server.crt
            keyfile /mosquitto/config/server.key
            """;

        _container = new ContainerBuilder(MosquittoImage)
            .WithPortBinding(8883, true)
            .WithResourceMapping(Encoding.UTF8.GetBytes(config), "/mosquitto/config/mosquitto.conf")
            .WithResourceMapping(Encoding.UTF8.GetBytes(caPem), "/mosquitto/config/ca.crt")
            .WithResourceMapping(Encoding.UTF8.GetBytes(serverCertPem), "/mosquitto/config/server.crt")
            .WithResourceMapping(Encoding.UTF8.GetBytes(serverKeyPem), "/mosquitto/config/server.key")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("mosquitto.*running"))
            .Build();

        await _container.StartAsync();
        Port = _container.GetMappedPublicPort(8883);
    }

    private static bool IsDockerUnavailable(Exception ex)
    {
        // Testcontainers throws ArgumentException with ParamName "DockerEndpointAuthConfig"
        // when Docker is not installed or not running.
        if (ex is ArgumentException { ParamName: "DockerEndpointAuthConfig" })
            return true;

        // Some versions may wrap the error differently; check type name as a fallback.
        var typeName = ex.GetType().Name;
        if (typeName.Contains("DockerUnavailableException"))
            return true;

        return false;
    }
}
