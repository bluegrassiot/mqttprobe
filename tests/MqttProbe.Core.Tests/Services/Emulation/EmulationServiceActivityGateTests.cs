using Microsoft.Extensions.Logging;
using MQTTnet;
using MqttProbe.Core.Models.Emulation;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Emulation;
using MqttProbe.Core.Services.Metrics;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Core.Services.Plugins.Pipeline;
using MqttProbe.Core.Services.Security;
using MqttProbe.Core.Services.Sparkplug;
using MqttProbe.Tests.Utilities;
using SparkplugNet.Core.Enumerations;
using SparkplugNet.Core.Node;
using SparkplugNet.VersionB.Data;

namespace MqttProbe.Core.Tests.Services.Emulation;

[TestFixture]
public class EmulationServiceActivityGateTests
{
    private string _filePath = null!;
    private SettingsDocument _document = null!;
    private IEmulatorSettings _emulatorSettings = null!;
    private ISparkplugNodeFactory _mockNodeFactory = null!;
    private IMqttManagedClient _mockMqttClient = null!;
    private ISessionState _mockSessionState = null!;
    private IUxMetricsService _mockMetrics = null!;
    private ICertificateAssetStore _mockCertStore = null!;
    private ICertificateSessionQuarantine _mockQuarantine = null!;
    private IAppHealthMetricsCollector _mockHealthCollector = null!;
    private PayloadPipeline _pipeline = null!;
    private ISparkplugNode _mockNode = null!;
    private RevocableSessionActivityGate _gate = null!;

    [SetUp]
    public async Task Setup()
    {
        _filePath = Path.Combine(Path.GetTempPath(), $"emu_gate_test_{Guid.NewGuid()}.json");
        _document = new SettingsDocument(_filePath);
        _emulatorSettings = new EmulatorSettings(_document);
        await new SettingsLoader(_document, new ConnectionSecrets(null, null), false, null)
            .LoadAsync();

        _mockSessionState = Substitute.For<ISessionState>();
        _mockSessionState.SelectedConnection.Returns(new Connection());

        await _emulatorSettings.SetEmulatorPublishIntervalAsync(_mockSessionState.SelectedConnection.Id, 120_000);
        _mockNodeFactory = Substitute.For<ISparkplugNodeFactory>();
        _mockMqttClient = Substitute.For<IMqttManagedClient>();
        _mockMqttClient.EnqueueAsync(Arg.Any<MqttApplicationMessage>()).Returns(Task.CompletedTask);
        _mockMetrics = Substitute.For<IUxMetricsService>();
        _mockCertStore = Substitute.For<ICertificateAssetStore>();
        _mockQuarantine = Substitute.For<ICertificateSessionQuarantine>();
        _mockHealthCollector = Substitute.For<IAppHealthMetricsCollector>();
        _mockHealthCollector.GetSnapshot().Returns(new AppHealthMetricsSnapshot(
            CpuUsagePercent: 0, ManagedHeapMb: 0,
            WorkingSetMb: 0, ThreadCount: 0, ThreadPoolQueueLength: 0,
            GcGen2Collections: 0, UptimeSeconds: 0));

        _mockMqttClient
            .When(x => x.DisconnectedAsync += Arg.Any<Func<MqttClientDisconnectedEventArgs, Task>>())
            .Do(_ => { });

        _mockNode = Substitute.For<ISparkplugNode>();
        _mockNode.Start(Arg.Any<SparkplugNodeOptions>()).Returns(Task.CompletedTask);
        _mockNode.Stop().Returns(Task.CompletedTask);
        _mockNode.PublishMetrics(Arg.Any<List<Metric>>()).Returns(Task.CompletedTask);
        _mockNode.PublishNodeDeathMessage().Returns(Task.CompletedTask);
        _mockNode.PublishDeviceBirthMessage(Arg.Any<string>(), Arg.Any<List<Metric>>()).Returns(Task.CompletedTask);
        _mockNode.PublishDeviceMetrics(Arg.Any<string>(), Arg.Any<List<Metric>>()).Returns(Task.CompletedTask);
        _mockNode.IsConnected.Returns(true);
        _mockNodeFactory.Create(Arg.Any<List<Metric>>(), Arg.Any<SparkplugSpecificationVersion>(),
                Arg.Any<CancellationToken>())
            .Returns(_mockNode);
        _mockNodeFactory.Create(
                Arg.Any<List<Metric>>(),
                Arg.Any<SparkplugSpecificationVersion>(),
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<Func<string, List<Metric>>>(),
                Arg.Any<CancellationToken>())
            .Returns(_mockNode);

        _gate = new RevocableSessionActivityGate();

        _pipeline = TestPipelineHelper.BuildBuiltInPipeline();
    }

    [TearDown]
    public void TearDown()
    {
        _mockMqttClient?.Dispose();
        _mockHealthCollector?.Dispose();
        _gate?.Dispose();
        _document?.Dispose();
        if (File.Exists(_filePath)) File.Delete(_filePath);
    }

    private EmulationService CreateService()
    {
        var svc = new EmulationService(
            _emulatorSettings,
            _mockNodeFactory,
            _mockSessionState,
            _mockMqttClient,
            _mockMetrics,
            _mockCertStore,
            _mockQuarantine,
            _pipeline,
            Substitute.For<ILogger<EmulationService>>(),
            _mockHealthCollector,
            _gate);
        svc.SetConnection(_mockSessionState.SelectedConnection.Id);
        return svc;
    }

    [Test]
    public void Constructor_accepts_gate_parameter()
    {
        var act = () => CreateService();
        act.Should().NotThrow();
    }

    [Test]
    public async Task StartAsync_before_revoke_starts_emulation()
    {
        var service = CreateService();
        await service.AddNodeAsync(new EmulatorNodeConfig { Type = EmulatorNodeType.SparkplugB, NodeId = "Node-1" });

        await service.StartAsync();

        service.IsRunning.Should().BeTrue();
        await service.StopAsync();
        service.Dispose();
    }

    [Test]
    public async Task StartAsync_after_revoke_throws_InvalidOperationException()
    {
        var service = CreateService();
        await service.AddNodeAsync(new EmulatorNodeConfig { Type = EmulatorNodeType.SparkplugB, NodeId = "Node-1" });
        _gate.Revoke();

        var act = () => service.StartAsync();
        await act.Should().ThrowExactlyAsync<InvalidOperationException>();
    }

    [Test]
    public async Task Revoke_while_first_runner_starts_prevents_later_starts_and_stops_first()
    {
        var service = CreateService();
        await service.AddNodeAsync(new EmulatorNodeConfig { Type = EmulatorNodeType.SparkplugB, NodeId = "Node-1" });
        await service.AddNodeAsync(new EmulatorNodeConfig { Type = EmulatorNodeType.SparkplugB, NodeId = "Node-2" });

        var startCount = 0;
        _mockNode.Start(Arg.Any<SparkplugNodeOptions>())
            .Returns(ci =>
            {
                startCount++;
                if (startCount == 1)
                    _gate.Revoke();
                return Task.CompletedTask;
            });

        var act = () => service.StartAsync();
        await act.Should().ThrowExactlyAsync<InvalidOperationException>();

        // Only the first runner should have been started; the second should have been skipped.
        startCount.Should().Be(1);
        service.IsRunning.Should().BeFalse();
        service.Dispose();
    }

    [Test]
    public async Task Publish_loop_exits_after_revoke()
    {
        await _emulatorSettings.SetEmulatorPublishIntervalAsync(_mockSessionState.SelectedConnection.Id, 50);
        var service = CreateService();
        await service.AddNodeAsync(new EmulatorNodeConfig { Type = EmulatorNodeType.SparkplugB, NodeId = "Node-1" });
        await service.StartAsync();
        service.IsRunning.Should().BeTrue();

        // Wait for at least one publish tick so we know the loop was actively running.
        await _mockNode.ReceivedWithAnyArgs().PublishMetrics(Arg.Any<List<Metric>>());

        _mockNode.ClearReceivedCalls();
        _gate.Revoke();

        // Linked CTS cancellation runs with the revocation callbacks, so wait for
        // them before reading IsRunning.
        await _gate.RevocationCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        service.IsRunning.Should().BeFalse();

        // Give the cancelled Task.Delay time to unwind, then prove no further tick ran.
        await Task.Delay(200);
        await _mockNode.DidNotReceiveWithAnyArgs().PublishMetrics(Arg.Any<List<Metric>>());
        service.Dispose();
    }

    [Test]
    public async Task Sparkplug_options_receive_revocation_token()
    {
        SparkplugNodeOptions? capturedOptions = null;
        _mockNode.Start(Arg.Do<SparkplugNodeOptions>(o => capturedOptions = o))
            .Returns(Task.CompletedTask);

        var service = CreateService();
        await service.AddNodeAsync(new EmulatorNodeConfig { Type = EmulatorNodeType.SparkplugB, NodeId = "Node-1" });
        await service.StartAsync();

        capturedOptions.Should().NotBeNull();
        capturedOptions!.CancellationToken.Should().Be(_gate.RevocationToken);

        await service.StopAsync();
        service.Dispose();
    }

    [Test]
    public async Task StopAsync_after_revoke_completes_without_error()
    {
        var service = CreateService();
        await service.AddNodeAsync(new EmulatorNodeConfig { Type = EmulatorNodeType.SparkplugB, NodeId = "Node-1" });
        await service.StartAsync();

        _gate.Revoke();

        var act = () => service.StopAsync();
        await act.Should().NotThrowAsync();
        service.IsRunning.Should().BeFalse();
        service.Dispose();
    }

    [Test]
    public async Task StopAsync_after_revoke_is_idempotent()
    {
        var service = CreateService();
        await service.AddNodeAsync(new EmulatorNodeConfig { Type = EmulatorNodeType.SparkplugB, NodeId = "Node-1" });
        await service.StartAsync();

        _gate.Revoke();

        await service.StopAsync();
        await service.StopAsync();

        service.IsRunning.Should().BeFalse();
        service.Dispose();
    }

    [Test]
    public async Task Concurrent_StopAsync_one_teardown()
    {
        var service = CreateService();
        await service.AddNodeAsync(new EmulatorNodeConfig { Type = EmulatorNodeType.SparkplugB, NodeId = "Node-1" });
        await service.StartAsync();

        var task1 = service.StopAsync();
        var task2 = service.StopAsync();
        await Task.WhenAll(task1, task2);

        service.IsRunning.Should().BeFalse();
        service.Dispose();
    }

    [Test]
    public void Dispose_after_revoke_does_not_throw()
    {
        var service = CreateService();

        _gate.Revoke();

        var act = () => service.Dispose();
        act.Should().NotThrow();
    }

    [Test]
    public async Task Revoke_after_final_runner_starts_rolls_back_all()
    {
        var service = CreateService();
        await service.AddNodeAsync(new EmulatorNodeConfig { Type = EmulatorNodeType.SparkplugB, NodeId = "Node-1" });
        await service.AddNodeAsync(new EmulatorNodeConfig { Type = EmulatorNodeType.SparkplugB, NodeId = "Node-2" });

        var startCount = 0;
        _mockNode.Start(Arg.Any<SparkplugNodeOptions>())
            .Returns(ci =>
            {
                startCount++;
                // Revoke after the last (second) runner starts.
                if (startCount == 2)
                    _gate.Revoke();
                return Task.CompletedTask;
            });

        var act = () => service.StartAsync();
        await act.Should().ThrowExactlyAsync<InvalidOperationException>();

        // Both runners should have been started, but the final recheck triggers rollback.
        startCount.Should().Be(2);
        service.IsRunning.Should().BeFalse();
        // Both runners should have been stopped during rollback.
        await _mockNode.Received(2).Stop();
        service.Dispose();
    }
}
