using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;
using MqttProbe.Models.Emulation;
using MqttProbe.Models.Mqtt;
using MqttProbe.Services.Emulation;
using MqttProbe.Services.Mqtt;
using MqttProbe.Services.Security;
using MqttProbe.Services.Sparkplug;
using MqttProbe.TestInfrastructure.Fixtures;
using SparkplugNet.VersionB;
using SparkplugNet.VersionB.Data;

namespace MqttProbe.IntegrationTests.Integration;

[TestFixture]
public class MtlsIntegrationTests
{
    private static MtlsBrokerFixture? _broker;

    [OneTimeSetUp]
    public async Task Setup() => _broker = await MtlsBrokerFixture.StartAsync();

    [OneTimeTearDown]
    public async Task Teardown()
    {
        if (_broker is not null)
            await _broker.DisposeAsync();
    }

    [Test]
    public async Task DirectMqtt_PfxCert_ConnectsWithTls()
    {
        var certStore = new CertificateAssetStore(
            new InMemoryEnvelopeKeyStore(),
            Path.GetTempPath(),
            Substitute.For<ILogger<CertificateAssetStore>>());

        var conn = new Connection
        {
            Name = "Test",
            Host = "127.0.0.1",
            Port = _broker!.Port,
            UseTls = true,
            AllowUntrustedCertificate = true,
            ClientId = "test-pfx"
        };
        var connAssetId = await certStore.ImportAsync(conn.Id,
            new CertificateImportRequest(CertificateInputMode.Pfx, _broker.ClientPfxBytes, null, _broker.ClientPfxPassword));
        conn.ClientCertificateAssetId = connAssetId;

        var builder = new MqttOptionsBuilder(certStore);
        var resource = new CertificateSessionResource();
        var options = await builder.BuildAsync(conn, resource);

        using var client = new MqttFactory().CreateManagedMqttClient();
        var connected = new TaskCompletionSource<bool>();
        client.ConnectedAsync += _ => { connected.TrySetResult(true); return Task.CompletedTask; };

        await client.StartAsync(options);
        var result = await Task.WhenAny(connected.Task, Task.Delay(10000));
        result.Should().Be(connected.Task, "client should connect within 10 seconds");

        await client.StopAsync();
        resource.Dispose();
    }

    [Test]
    public async Task DirectMqtt_PemCert_ConnectsWithTls()
    {
        var certStore = new CertificateAssetStore(
            new InMemoryEnvelopeKeyStore(),
            Path.GetTempPath(),
            Substitute.For<ILogger<CertificateAssetStore>>());

        var certPem = System.Text.Encoding.UTF8.GetBytes(_broker!.ClientCert.ExportCertificatePem());
        var keyPem = System.Text.Encoding.UTF8.GetBytes(_broker.ClientCert.GetRSAPrivateKey()!.ExportPkcs8PrivateKeyPem());

        var conn = new Connection
        {
            Name = "Test",
            Host = "127.0.0.1",
            Port = _broker.Port,
            UseTls = true,
            AllowUntrustedCertificate = true,
            ClientId = "test-pem"
        };
        var connAssetId = await certStore.ImportAsync(conn.Id,
            new CertificateImportRequest(CertificateInputMode.Pem, certPem, keyPem, null));
        conn.ClientCertificateAssetId = connAssetId;

        var builder = new MqttOptionsBuilder(certStore);
        var resource = new CertificateSessionResource();
        var options = await builder.BuildAsync(conn, resource);

        using var client = new MqttFactory().CreateManagedMqttClient();
        var connected = new TaskCompletionSource<bool>();
        client.ConnectedAsync += _ => { connected.TrySetResult(true); return Task.CompletedTask; };

        await client.StartAsync(options);
        var result = await Task.WhenAny(connected.Task, Task.Delay(10000));
        result.Should().Be(connected.Task);

        await client.StopAsync();
        resource.Dispose();
    }

    [Test]
    public async Task DirectMqtt_MissingCertAsset_ThrowsUnavailable()
    {
        var certStore = Substitute.For<ICertificateAssetStore>();
        certStore.LoadAsync(Arg.Any<Guid>(), Arg.Any<string>()).Returns((ClientCertificateBundle?)null);

        var conn = new Connection
        {
            Name = "Test",
            Host = "127.0.0.1",
            Port = _broker!.Port,
            UseTls = true,
            AllowUntrustedCertificate = true,
            ClientCertificateAssetId = Guid.NewGuid().ToString("D"),
            ClientId = "test-missing"
        };

        var builder = new MqttOptionsBuilder(certStore);
        var resource = new CertificateSessionResource();

        var act = () => builder.BuildAsync(conn, resource);
        await act.Should().ThrowAsync<CertificateAssetUnavailableException>();
    }

    [Test]
    public async Task Sparkplug_MtlsCert_ConnectsAndPublishesBirth()
    {
        var certStore = new CertificateAssetStore(
            new InMemoryEnvelopeKeyStore(),
            Path.GetTempPath(),
            Substitute.For<ILogger<CertificateAssetStore>>());

        var conn = new Connection
        {
            Name = "Test",
            Host = "127.0.0.1",
            Port = _broker!.Port,
            UseTls = true,
            AllowUntrustedCertificate = true,
            ClientId = "sparkplug-test"
        };

        var connAssetId = await certStore.ImportAsync(conn.Id,
            new CertificateImportRequest(CertificateInputMode.Pfx, _broker.ClientPfxBytes, null, _broker.ClientPfxPassword));
        conn.ClientCertificateAssetId = connAssetId;

        var config = new EmulatorNodeConfig
        {
            Type = EmulatorNodeType.SparkplugB,
            NodeId = "TestNode",
            GroupId = "TestGroup"
        };

        // Verifier subscribes FIRST with its own client cert (broker requires mTLS)
        using var verifier = new MqttFactory().CreateMqttClient();
        var verifierOpts = new MqttClientOptionsBuilder()
            .WithTcpServer("127.0.0.1", _broker.Port)
            .WithTlsOptions(o => o
                .WithClientCertificates(new X509Certificate2Collection(
                    X509CertificateLoader.LoadPkcs12(_broker.ClientPfxBytes, _broker.ClientPfxPassword, X509KeyStorageFlags.Exportable)))
                .WithCertificateValidationHandler(ctx =>
                {
                    using var chain = new X509Chain();
                    chain.ChainPolicy.ExtraStore.Add(_broker.CaCert);
                    chain.ChainPolicy.VerificationFlags =
                        X509VerificationFlags.AllowUnknownCertificateAuthority
                        | X509VerificationFlags.IgnoreCertificateAuthorityRevocationUnknown
                        | X509VerificationFlags.IgnoreEndRevocationUnknown
                        | X509VerificationFlags.IgnoreRootRevocationUnknown;
                    return chain.Build((X509Certificate2)ctx.Certificate);
                }))
            .Build();
        await verifier.ConnectAsync(verifierOpts);
        await verifier.SubscribeAsync("spBv1.0/#");

        var birthReceived = new TaskCompletionSource<bool>();
        verifier.ApplicationMessageReceivedAsync += e =>
        {
            var msg = e.ApplicationMessage;
            var topic = msg.Topic;
            var parts = topic.Split('/');
            if (parts.Length == 4
                && parts[0] == "spBv1.0"
                && parts[1] == "TestGroup"
                && parts[2] == "NBIRTH"
                && parts[3] == "TestNode")
            {
                var payload = msg.PayloadSegment;
                if (payload.Count > 0)
                {
                    try
                    {
                        var sparkplugAsm = typeof(Payload).Assembly;
                        var converterType = sparkplugAsm.GetType("SparkplugNet.VersionB.PayloadConverter")!;
                        var protoPayloadType = sparkplugAsm.GetType("SparkplugNet.VersionB.ProtoBuf.ProtoBufPayload")!;
                        var helperType = sparkplugAsm.GetType("SparkplugNet.Core.PayloadHelper")!;

                        var deserializeMethod = helperType
                            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
                            .First(m => m.Name == "Deserialize" && m.GetParameters().Length == 1);
                        var genericDeserialize = deserializeMethod.MakeGenericMethod(protoPayloadType);
                        var protoPayload = genericDeserialize.Invoke(null, [payload.ToArray()])!;

                        var convertMethod = converterType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                            .First(m => m.Name == "ConvertVersionBPayload"
                                        && m.GetParameters()[0].ParameterType != typeof(Payload));
                        var sparkplugPayload = (Payload)convertMethod.Invoke(null, [protoPayload])!;

                        if (sparkplugPayload.Metrics.Count > 0
                            && sparkplugPayload.Metrics.Any(m =>
                                !string.IsNullOrEmpty(m.Name) && m.Value is not null))
                        {
                            birthReceived.TrySetResult(true);
                        }
                    }
                    catch
                    {
                        // Invalid protobuf or reflection failure — don't set result
                    }
                }
            }
            return Task.CompletedTask;
        };

        var nodeFactory = new SparkplugNodeFactory();
        var runner = new SparkplugNodeRunner(
            config, nodeFactory, conn, [],
            certStore, Substitute.For<ICertificateSessionQuarantine>(),
            Substitute.For<ILogger>());

        await runner.StartAsync();
        runner.Status.Should().Be(NodeRuntimeStatus.Connected);

        var result = await Task.WhenAny(birthReceived.Task, Task.Delay(15000));
        result.Should().Be(birthReceived.Task, "NBIRTH should be published within 15 seconds");

        await runner.StopAsync();
        await verifier.DisconnectAsync();
    }
}
