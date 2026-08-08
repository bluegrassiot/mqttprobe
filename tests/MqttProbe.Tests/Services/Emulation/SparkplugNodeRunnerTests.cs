using Microsoft.Extensions.Logging;
using MqttProbe.Models.Emulation;
using MqttProbe.Models.Mqtt;
using MqttProbe.Services.Emulation;
using MqttProbe.Services.Security;
using MqttProbe.Services.Sparkplug;
using MqttProbe.Tests.Services.Security.TestHelpers;
using SparkplugNet.Core.Enumerations;
using SparkplugNet.Core.Node;
using SparkplugNet.VersionB.Data;

namespace MqttProbe.Shared.Tests.Services.Emulation;

[TestFixture]
public class SparkplugNodeRunnerTests
{
    private ISparkplugNodeFactory _factory = null!;
    private ICertificateAssetStore _certStore = null!;
    private ICertificateSessionQuarantine _quarantine = null!;
    private ILogger _logger = null!;

    [SetUp]
    public void Setup()
    {
        _factory = Substitute.For<ISparkplugNodeFactory>();
        _certStore = Substitute.For<ICertificateAssetStore>();
        _quarantine = Substitute.For<ICertificateSessionQuarantine>();
        _logger = Substitute.For<ILogger>();
        _logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
    }

    private static Connection TcpConnection() => new()
    {
        Name = "Test",
        Host = "broker.local",
        Port = 1883,
        Protocol = Protocol.Mqtt,
        ClientId = "primary-client",
        User = "u",
        Password = "p",
        ReconnectDelay = 5
    };

    private static EmulatorNodeConfig NodeConfig(bool useAliases = false)
    {
        var config = new EmulatorNodeConfig
        {
            NodeId = "Press-01",
            GroupId = "Plant",
            UseMetricAliases = useAliases
        };
        config.Devices.Add(new EmulatorDeviceConfig
        {
            DeviceId = "Device-1",
            Metrics = [new EmulatorMetricConfig { Name = "Temp", ValueType = MetricValueType.Double }]
        });
        return config;
    }

    // Substituted as IDisposable too: the runner disposes the node through that cast,
    // and the failure paths under test hinge on Dispose throwing.
    private ISparkplugNode SetupNode(bool isConnected = true)
    {
        var node = Substitute.For<ISparkplugNode, IDisposable>();
        node.IsConnected.Returns(isConnected);
        _factory.Create(Arg.Any<IReadOnlyList<Metric>>(), Arg.Any<SparkplugSpecificationVersion>())
            .Returns(node);
        _factory.Create(
                Arg.Any<IReadOnlyList<Metric>>(),
                Arg.Any<SparkplugSpecificationVersion>(),
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<Func<string, IReadOnlyList<Metric>>>())
            .Returns(node);
        return node;
    }

    private SparkplugNodeRunner Runner(
        EmulatorNodeConfig config, Connection connection, params Metric[] knownMetrics) =>
        new(config, _factory, connection, knownMetrics, _certStore, _quarantine, _logger);

    [Test]
    public async Task StartAsync_WithCertificateAsset_PassesCertificateToNodeOptions()
    {
        var conn = TcpConnection();
        conn.UseTls = true;
        conn.ClientCertificateAssetId = "asset-1";
        using var cert = TestCertFactory.CreateRsaCert();
        _certStore.LoadAsync(conn.Id, "asset-1").Returns(new ClientCertificateBundle(cert));

        var node = SetupNode();
        SparkplugNodeOptions? captured = null;
        node.When(n => n.Start(Arg.Any<SparkplugNodeOptions>()))
            .Do(ci => captured = ci.Arg<SparkplugNodeOptions>());

        var runner = Runner(NodeConfig(), conn);
        await runner.StartAsync();

        runner.Status.Should().Be(NodeRuntimeStatus.Connected);
        captured.Should().NotBeNull();
        var clientCerts = captured!.MqttTlsOptions?.ClientCertificatesProvider?.GetCertificates();
        clientCerts.Should().NotBeNull();
        clientCerts.Count.Should().Be(1);
    }

    [Test]
    public async Task StartAsync_MissingCertificateAsset_ThrowsAndReportsError()
    {
        var conn = TcpConnection();
        conn.UseTls = true;
        conn.ClientCertificateAssetId = "missing";
        _certStore.LoadAsync(conn.Id, "missing").Returns((ClientCertificateBundle?)null);

        var runner = Runner(NodeConfig(), conn);

        var act = () => runner.StartAsync();

        await act.Should().ThrowAsync<CertificateAssetUnavailableException>();
        runner.Status.Should().Be(NodeRuntimeStatus.Error);
    }

    [Test]
    public async Task StartAsync_WhenStartAndDisposeBothFail_QuarantinesCertificate()
    {
        var conn = TcpConnection();
        conn.UseTls = true;
        conn.ClientCertificateAssetId = "asset-1";
        using var cert = TestCertFactory.CreateRsaCert();
        _certStore.LoadAsync(conn.Id, "asset-1").Returns(new ClientCertificateBundle(cert));

        var node = SetupNode();
        node.Start(Arg.Any<SparkplugNodeOptions>())
            .Returns(Task.FromException(new InvalidOperationException("connect failed")));
        ((IDisposable)node).When(d => d.Dispose())
            .Do(_ => throw new InvalidOperationException("dispose failed"));

        var runner = Runner(NodeConfig(), conn);

        var act = () => runner.StartAsync();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("connect failed");
        runner.Status.Should().Be(NodeRuntimeStatus.Error);
        _quarantine.Received(1).Quarantine(
            Arg.Any<CertificateSessionResource>(),
            Arg.Is<string>(reason => reason != null && reason.Contains("dispose failed", StringComparison.Ordinal)));
    }

    [Test]
    public async Task StartAsync_AfterFailedStop_RefusesToRestart()
    {
        var node = SetupNode();
        var runner = Runner(NodeConfig(), TcpConnection());
        await runner.StartAsync();

        node.Stop().Returns(Task.FromException(new InvalidOperationException("stop failed")));
        var stop = () => runner.StopAsync();
        await stop.Should().ThrowAsync<InvalidOperationException>().WithMessage("stop failed");
        runner.Status.Should().Be(NodeRuntimeStatus.Error);

        var restart = () => runner.StartAsync();

        await restart.Should().ThrowAsync<InvalidOperationException>().WithMessage("*faulted*");
    }

    [Test]
    public async Task StopAsync_WhenStopFails_QuarantinesCertificateAndPublishesDeath()
    {
        var conn = TcpConnection();
        conn.UseTls = true;
        conn.ClientCertificateAssetId = "asset-1";
        using var cert = TestCertFactory.CreateRsaCert();
        _certStore.LoadAsync(conn.Id, "asset-1").Returns(new ClientCertificateBundle(cert));

        var node = SetupNode();
        var runner = Runner(NodeConfig(), conn);
        await runner.StartAsync();

        node.Stop().Returns(Task.FromException(new InvalidOperationException("stop failed")));

        var act = () => runner.StopAsync();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("stop failed");
        _quarantine.Received(1).Quarantine(
            Arg.Any<CertificateSessionResource>(),
            Arg.Is<string>(reason => reason != null && reason.Contains("StopAsync failed", StringComparison.Ordinal)));
        // Once in the normal path, once in the best-effort teardown after the failure.
        await node.Received(2).PublishNodeDeathMessage();
    }

    [Test]
    public async Task StopAsync_WhenDeathMessageFails_StillStopsCleanly()
    {
        var node = SetupNode();
        node.PublishNodeDeathMessage()
            .Returns(Task.FromException(new InvalidOperationException("broker gone")));

        var runner = Runner(NodeConfig(), TcpConnection());
        await runner.StartAsync();

        await runner.StopAsync();

        runner.Status.Should().Be(NodeRuntimeStatus.Idle);
        await node.Received(1).Stop();
    }

    [Test]
    public async Task StartAsync_WithAliases_DeviceBirthCallbackReturnsNamedAliasedMetrics()
    {
        var node = SetupNode();
        Func<string, IReadOnlyList<Metric>>? birthCallback = null;
        _factory.Create(
                Arg.Any<IReadOnlyList<Metric>>(),
                Arg.Any<SparkplugSpecificationVersion>(),
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<Func<string, IReadOnlyList<Metric>>>())
            .Returns(ci =>
            {
                birthCallback = ci.Arg<Func<string, IReadOnlyList<Metric>>>();
                return node;
            });

        var runner = Runner(NodeConfig(useAliases: true), TcpConnection());
        await runner.StartAsync();

        birthCallback.Should().NotBeNull();
        var birthMetrics = birthCallback!("Device-1");

        birthMetrics.Should().HaveCount(1);
        birthMetrics[0].Name.Should().Be("Temp");
        birthMetrics[0].Alias.Should().Be(1);
    }

    [Test]
    public async Task PublishTickAsync_WithAliases_SendsAliasOnlyDeviceMetrics()
    {
        var node = SetupNode();
        var runner = Runner(
            NodeConfig(useAliases: true), TcpConnection(), new Metric("Health", DataType.Double, 1.0));
        await runner.StartAsync();

        IReadOnlyList<Metric>? published = null;
        node.When(n => n.PublishDeviceMetrics(Arg.Any<string>(), Arg.Any<IReadOnlyList<Metric>>()))
            .Do(ci => published = ci.Arg<IReadOnlyList<Metric>>());

        await runner.PublishTickAsync(1.0, [new Metric("Health", DataType.Double, 2.0)]);

        published.Should().NotBeNull();
        published!.Should().HaveCount(1);
        published[0].Name.Should().BeNull();
        published[0].Alias.Should().Be(1);
    }
}
