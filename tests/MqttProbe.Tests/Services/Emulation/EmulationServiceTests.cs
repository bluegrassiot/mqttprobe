using System.Diagnostics;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;
using MqttProbe.Models.Emulation;
using MqttProbe.Models.Mqtt;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Emulation;
using MqttProbe.Services.Metrics;
using MqttProbe.Services.Mqtt;
using MqttProbe.Services.Security;
using MqttProbe.Services.Sparkplug;
using SparkplugNet.Core.Enumerations;
using SparkplugNet.Core.Node;
using SparkplugNet.VersionB;
using SparkplugNet.VersionB.Data;

namespace MqttProbe.Shared.Tests.Services.Emulation;

[TestFixture]
public class EmulationServiceTests
{
    private static readonly string[] _expectedHealthMetricNames =
    [
        "CPU Usage (%)",
        "Managed Heap (MB)",
        "Working Set (MB)",
        "Thread Count",
        "ThreadPool Queue",
        "GC Gen2 Collections",
        "Uptime (s)",
        "Publishers Online",
        "Publish Cycles"
    ];

    private string _filePath = null!;
    private ISettingsStore _settingsStore = null!;
    private ISparkplugNodeFactory _mockNodeFactory = null!;
    private IManagedMqttClient _mockMqttClient = null!;
    private ISessionState _mockSessionState = null!;
    private IUxMetricsService _mockMetrics = null!;
    private ICertificateAssetStore _mockCertStore = null!;
    private ICertificateSessionQuarantine _mockQuarantine = null!;
    private IAppHealthMetricsCollector _mockHealthCollector = null!;
    private EmulationService _service = null!;
    private Func<MqttClientDisconnectedEventArgs, Task>? _disconnectedHandler;

    [SetUp]
    public async Task Setup()
    {
        _filePath = Path.Combine(Path.GetTempPath(), $"emulation_service_test_{Guid.NewGuid()}.json");
        var settingsStore = new SettingsStore(_filePath);
        _settingsStore = settingsStore;
        await settingsStore.LoadAsync();

        _mockSessionState = Substitute.For<ISessionState>();
        _mockSessionState.SelectedConnection.Returns(new Connection());

        // A long interval keeps the background loop from ticking during a test,
        // so the single inline tick from StartAsync is the only publish observed.
        await settingsStore.SetEmulatorPublishIntervalAsync(_mockSessionState.SelectedConnection.Id, 120_000);
        _mockNodeFactory = Substitute.For<ISparkplugNodeFactory>();
        _mockMqttClient = Substitute.For<IManagedMqttClient>();
        _mockMqttClient.EnqueueAsync(Arg.Any<MqttApplicationMessage>()).Returns(Task.CompletedTask);
        _mockMetrics = Substitute.For<IUxMetricsService>();
        _mockCertStore = Substitute.For<ICertificateAssetStore>();
        _mockQuarantine = Substitute.For<ICertificateSessionQuarantine>();
        _mockHealthCollector = Substitute.For<IAppHealthMetricsCollector>();
        _mockHealthCollector.GetSnapshot().Returns(new AppHealthMetricsSnapshot(
            Available: true, CpuUsagePercent: 0, ManagedHeapMb: 0,
            WorkingSetMb: 0, ThreadCount: 0, ThreadPoolQueueLength: 0,
            GcGen2Collections: 0, UptimeSeconds: 0));

        _disconnectedHandler = null;
        _mockMqttClient
            .When(x => x.DisconnectedAsync += Arg.Any<Func<MqttClientDisconnectedEventArgs, Task>>())
            .Do(x => _disconnectedHandler = x.Arg<Func<MqttClientDisconnectedEventArgs, Task>>());

        SetupSuccessfulNode();

        _service = new EmulationService(
            settingsStore,
            _mockNodeFactory,
            _mockSessionState,
            _mockMqttClient,
            _mockMetrics,
            _mockCertStore,
            _mockQuarantine,
            Substitute.For<ILogger<EmulationService>>(),
            _mockHealthCollector);
        _service.SetConnection(_mockSessionState.SelectedConnection.Id);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _service.StopAsync();
        _service.Dispose();
        _mockMqttClient.Dispose();
        _mockHealthCollector.Dispose();
        if (File.Exists(_filePath)) File.Delete(_filePath);
    }

    private ISparkplugNode SetupSuccessfulNode(bool isConnected = true)
    {
        var node = Substitute.For<ISparkplugNode>();
        node.Start(Arg.Any<SparkplugNodeOptions>()).Returns(Task.CompletedTask);
        node.Stop().Returns(Task.CompletedTask);
        node.PublishMetrics(Arg.Any<List<Metric>>()).Returns(Task.CompletedTask);
        node.PublishNodeDeathMessage().Returns(Task.CompletedTask);
        node.PublishDeviceBirthMessage(Arg.Any<string>(), Arg.Any<List<Metric>>()).Returns(Task.CompletedTask);
        node.PublishDeviceMetrics(Arg.Any<string>(), Arg.Any<List<Metric>>()).Returns(Task.CompletedTask);
        node.IsConnected.Returns(isConnected);
        _mockNodeFactory.Create(Arg.Any<List<Metric>>(), Arg.Any<SparkplugSpecificationVersion>())
            .Returns(node);
        _mockNodeFactory.Create(
                Arg.Any<List<Metric>>(),
                Arg.Any<SparkplugSpecificationVersion>(),
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<Func<string, List<Metric>>>())
            .Returns(node);
        return node;
    }

    private static EmulatorNodeConfig SparkplugNode(string nodeId = "Node-1", params string[] deviceMetricNames)
    {
        var node = new EmulatorNodeConfig { Type = EmulatorNodeType.SparkplugB, NodeId = nodeId };
        if (deviceMetricNames.Length > 0)
            node.Devices.Add(new EmulatorDeviceConfig
            {
                DeviceId = "Device-1",
                Metrics = deviceMetricNames.Select(n => new EmulatorMetricConfig { Name = n }).ToList()
            });
        return node;
    }

    [Test]
    public async Task Nodes_DelegatesToStore()
    {
        await _service.AddNodeAsync(new EmulatorNodeConfig { NodeId = "Press-01" });

        _service.Nodes.Should().HaveCount(1);
        _service.Nodes[0].NodeId.Should().Be("Press-01");
        _settingsStore.GetEmulatorNodes(_mockSessionState.SelectedConnection.Id).Should().HaveCount(1);
    }

    [Test]
    public async Task SetPublishIntervalAsync_DelegatesToStore()
    {
        await _service.SetPublishIntervalAsync(750);

        _service.PublishIntervalMs.Should().Be(750);
        _settingsStore.GetEmulatorPublishIntervalMs(_mockSessionState.SelectedConnection.Id).Should().Be(750);
    }

    [Test]
    public async Task StartAsync_SetsIsRunning()
    {
        await _service.AddNodeAsync(SparkplugNode());

        await _service.StartAsync();

        _service.IsRunning.Should().BeTrue();
    }

    [Test]
    public async Task StopAsync_ClearsIsRunning()
    {
        await _service.AddNodeAsync(SparkplugNode());
        await _service.StartAsync();

        await _service.StopAsync();

        _service.IsRunning.Should().BeFalse();
    }

    [Test]
    public async Task StartAsync_WhileRunning_DoesNotStartTwice()
    {
        await _service.AddNodeAsync(SparkplugNode());
        await _service.StartAsync();

        await _service.StartAsync();

        _mockNodeFactory.Received(1).Create(Arg.Any<List<Metric>>(), Arg.Any<SparkplugSpecificationVersion>());
    }

    [Test]
    public async Task StartAsync_StartsOneSparkplugNodePerSparkplugConfig()
    {
        var node = SetupSuccessfulNode();
        await _service.AddNodeAsync(SparkplugNode("Node-1"));
        await _service.AddNodeAsync(SparkplugNode("Node-2"));

        await _service.StartAsync();

        _mockNodeFactory.Received(2).Create(Arg.Any<List<Metric>>(), Arg.Any<SparkplugSpecificationVersion>());
        await node.Received(2).Start(Arg.Any<SparkplugNodeOptions>());
    }

    [Test]
    public async Task StartAsync_PassesGroupAndNodeIdentifiersToNodeOptions()
    {
        var node = SetupSuccessfulNode();
        SparkplugNodeOptions? captured = null;
        node.Start(Arg.Do<SparkplugNodeOptions>(o => captured = o)).Returns(Task.CompletedTask);
        var config = SparkplugNode("Press-01");
        config.GroupId = "BoilerRoom";
        await _service.AddNodeAsync(config);

        await _service.StartAsync();

        captured.Should().NotBeNull();
        captured!.GroupIdentifier.Should().Be("BoilerRoom");
        captured.EdgeNodeIdentifier.Should().Be("Press-01");
    }

    [Test]
    public async Task StartAsync_PublishesDeviceBirthPerDeviceWithItsOwnMetricList()
    {
        var node = SetupSuccessfulNode();
        var births = new List<(string DeviceId, List<string> MetricNames)>();
        node.PublishDeviceBirthMessage(
                Arg.Any<string>(),
                Arg.Any<List<Metric>>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci => births.Add((ci.ArgAt<string>(0), ci.ArgAt<List<Metric>>(1).Select(m => m.Name).ToList())));

        var config = new EmulatorNodeConfig { Type = EmulatorNodeType.SparkplugB, NodeId = "Node-1" };
        config.Devices.Add(new EmulatorDeviceConfig
        {
            DeviceId = "Flow-1",
            Metrics = [new EmulatorMetricConfig { Name = "Flow Rate" }, new EmulatorMetricConfig { Name = "Pressure" }]
        });
        config.Devices.Add(new EmulatorDeviceConfig
        {
            DeviceId = "Valve-1",
            Metrics = [new EmulatorMetricConfig { Name = "Position" }]
        });
        await _service.AddNodeAsync(config);

        await _service.StartAsync();

        births.Should().HaveCount(2);
        births.Should().ContainSingle(b => b.DeviceId == "Flow-1")
            .Which.MetricNames.Should().BeEquivalentTo("Flow Rate", "Pressure");
        births.Should().ContainSingle(b => b.DeviceId == "Valve-1")
            .Which.MetricNames.Should().BeEquivalentTo("Position");
    }

    [Test]
    public async Task StartAsync_FirstTick_PublishesNdataWithHealthMetricNames()
    {
        var node = SetupSuccessfulNode();
        List<string>? publishedNames = null;
        node.PublishMetrics(Arg.Do<List<Metric>>(m => publishedNames = m.Select(x => x.Name).ToList()))
            .Returns(Task.CompletedTask);
        await _service.AddNodeAsync(SparkplugNode());

        await _service.StartAsync();

        publishedNames.Should().NotBeNull();
        publishedNames.Should().BeEquivalentTo(_expectedHealthMetricNames);
    }

    [Test]
    public async Task StartAsync_FirstTick_PublishesDdataOnlyForDevicesWithMetrics()
    {
        var node = SetupSuccessfulNode();
        var config = SparkplugNode("Node-1", "Flow Rate");
        config.Devices.Add(new EmulatorDeviceConfig { DeviceId = "Empty-1" });
        await _service.AddNodeAsync(config);

        await _service.StartAsync();

        await node.Received(1).PublishDeviceMetrics("Device-1", Arg.Any<List<Metric>>());
        await node.DidNotReceive().PublishDeviceMetrics("Empty-1", Arg.Any<List<Metric>>());
    }

    [Test]
    public async Task StartAsync_GenericJsonNode_EnqueuesOneMessagePerDeviceOnRenderedTopic()
    {
        var messages = new List<MqttApplicationMessage>();
        await _mockMqttClient.EnqueueAsync(Arg.Do<MqttApplicationMessage>(messages.Add));

        var config = new EmulatorNodeConfig
        {
            Type = EmulatorNodeType.Generic,
            GroupId = "Plant1",
            NodeId = "Press-01",
            PayloadFormat = GenericPayloadFormat.Json
        };
        config.Devices.Add(new EmulatorDeviceConfig
        {
            DeviceId = "Sensor-1",
            Metrics = [new EmulatorMetricConfig { Name = "Flow Rate" }, new EmulatorMetricConfig { Name = "Valve Open", ValueType = MetricValueType.Boolean, Waveform = WaveformKind.FixedBoolean }]
        });
        await _service.AddNodeAsync(config);

        await _service.StartAsync();

        messages.Should().HaveCount(1);
        messages[0].Topic.Should().Be("Plant1/Press-01/Sensor-1");
        var payload = Encoding.UTF8.GetString(messages[0].PayloadSegment);
        payload.Should().Contain("\"Flow Rate\"");
        payload.Should().Contain("\"Valve Open\"");
        payload.Should().Contain("\"timestamp\"");
    }

    [Test]
    public async Task StartAsync_GenericPlainTextNode_EnqueuesOneMessagePerMetric()
    {
        var messages = new List<MqttApplicationMessage>();
        await _mockMqttClient.EnqueueAsync(Arg.Do<MqttApplicationMessage>(messages.Add));

        var config = new EmulatorNodeConfig
        {
            Type = EmulatorNodeType.Generic,
            GroupId = "Plant1",
            NodeId = "Press-01",
            PayloadFormat = GenericPayloadFormat.PlainText
        };
        config.Devices.Add(new EmulatorDeviceConfig
        {
            DeviceId = "Sensor-1",
            Metrics = [new EmulatorMetricConfig { Name = "Flow Rate" }, new EmulatorMetricConfig { Name = "Pressure" }]
        });
        await _service.AddNodeAsync(config);

        await _service.StartAsync();

        messages.Should().HaveCount(2);
        messages.Select(m => m.Topic).Should().BeEquivalentTo(
            "Plant1/Press-01/Sensor-1/Flow Rate",
            "Plant1/Press-01/Sensor-1/Pressure");
    }

    [Test]
    public async Task AddNodeAsync_WhileRunning_ThrowsInvalidOperation()
    {
        await _service.AddNodeAsync(SparkplugNode());
        await _service.StartAsync();

        var act = () => _service.AddNodeAsync(new EmulatorNodeConfig());

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Test]
    public async Task UpdateNodeAsync_WhileRunning_ThrowsInvalidOperation()
    {
        var config = SparkplugNode();
        await _service.AddNodeAsync(config);
        await _service.StartAsync();

        var act = () => _service.UpdateNodeAsync(config);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Test]
    public async Task RemoveNodeAsync_WhileRunning_ThrowsInvalidOperation()
    {
        var config = SparkplugNode();
        await _service.AddNodeAsync(config);
        await _service.StartAsync();

        var act = () => _service.RemoveNodeAsync(config.Id);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Test]
    public async Task RemoveAllNodesAsync_WhileRunning_ThrowsInvalidOperation()
    {
        await _service.AddNodeAsync(SparkplugNode());
        await _service.StartAsync();

        var act = () => _service.RemoveAllNodesAsync();

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Test]
    public async Task SetPublishIntervalAsync_WhileRunning_ThrowsInvalidOperation()
    {
        await _service.AddNodeAsync(SparkplugNode());
        await _service.StartAsync();

        var act = () => _service.SetPublishIntervalAsync(1000);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Test]
    public async Task DuplicateNodeAsync_WhileRunning_ThrowsInvalidOperation()
    {
        var config = SparkplugNode();
        await _service.AddNodeAsync(config);
        await _service.StartAsync();

        var act = () => _service.DuplicateNodeAsync(config.Id, 1);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Test]
    public async Task StopAsync_PublishesNodeDeathAndStopsNodes()
    {
        var node = SetupSuccessfulNode();
        await _service.AddNodeAsync(SparkplugNode());
        await _service.StartAsync();

        await _service.StopAsync();

        await node.Received(1).PublishNodeDeathMessage();
        await node.Received(1).Stop();
    }

    [Test]
    public async Task MainClientDisconnect_WhileRunning_StopsEmulation()
    {
        _disconnectedHandler.Should().NotBeNull("service should subscribe to DisconnectedAsync in constructor");
        await _service.AddNodeAsync(SparkplugNode());
        await _service.StartAsync();
        _service.IsRunning.Should().BeTrue();

        await _disconnectedHandler!(new MqttClientDisconnectedEventArgs(
            false, null!, MqttClientDisconnectReason.NormalDisconnection, null!, null!, null!));

        _service.IsRunning.Should().BeFalse();
    }

    [Test]
    public async Task DuplicateNodeAsync_NoSuffix_StartsNumberingAtTwo()
    {
        var source = SparkplugNode("FlowMeter");
        await _service.AddNodeAsync(source);

        var copies = await _service.DuplicateNodeAsync(source.Id, 3);

        copies.Select(c => c.NodeId).Should().Equal("FlowMeter-2", "FlowMeter-3", "FlowMeter-4");
        _service.Nodes.Should().HaveCount(4);
    }

    [Test]
    public async Task DuplicateNodeAsync_ZeroPaddedSuffix_PreservesPadding()
    {
        var source = SparkplugNode("Press-07");
        await _service.AddNodeAsync(source);

        var copies = await _service.DuplicateNodeAsync(source.Id, 2);

        copies.Select(c => c.NodeId).Should().Equal("Press-08", "Press-09");
    }

    [Test]
    public async Task DuplicateNodeAsync_SkipsNodeIdsAlreadyTakenInSameGroup()
    {
        var source = SparkplugNode("Pump-2");
        await _service.AddNodeAsync(source);
        await _service.AddNodeAsync(SparkplugNode("Pump-3"));

        var copies = await _service.DuplicateNodeAsync(source.Id, 1);

        copies.Single().NodeId.Should().Be("Pump-4");
    }

    [Test]
    public async Task DuplicateNodeAsync_DeepCopiesWithFreshGuids()
    {
        var source = SparkplugNode("Press-01", "Flow Rate");
        await _service.AddNodeAsync(source);

        var copy = (await _service.DuplicateNodeAsync(source.Id, 1)).Single();

        copy.Id.Should().NotBe(source.Id);
        copy.Devices.Single().Id.Should().NotBe(source.Devices.Single().Id);
        copy.Devices.Single().Metrics.Single().Id.Should().NotBe(source.Devices.Single().Metrics.Single().Id);
        copy.Devices.Single().DeviceId.Should().Be("Device-1");
        copy.Devices.Single().Metrics.Single().Name.Should().Be("Flow Rate");

        copy.Devices.Single().Metrics.Single().Max = 999;
        source.Devices.Single().Metrics.Single().Max.Should().NotBe(999);
    }

    [Test]
    public async Task Dispose_WhileRunning_DoesNotThrow_AndNoFurtherPublishesOccur()
    {
        await _service.AddNodeAsync(new EmulatorNodeConfig
        {
            Type = EmulatorNodeType.Generic,
            PayloadFormat = GenericPayloadFormat.PlainText,
            NodeId = "Sensor-1",
            Devices =
            {
                new EmulatorDeviceConfig
                {
                    DeviceId = "Dev-1",
                    Metrics = [new EmulatorMetricConfig { Name = "Value" }]
                }
            }
        });
        await _service.StartAsync();

        var publishCountBeforeDispose = (await _mockMqttClient.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IManagedMqttClient.EnqueueAsync))
            .ToAsyncEnumerable()
            .ToListAsync()).Count;

        var act = () => _service.Dispose();
        act.Should().NotThrow();

        _mockMqttClient.ClearReceivedCalls();
        await Task.Delay(50);
        await _mockMqttClient.DidNotReceive().EnqueueAsync(Arg.Any<MqttApplicationMessage>());
    }

    [Test]
    public void GetStatus_BeforeStart_ReturnsIdle()
    {
        _service.GetStatus(Guid.NewGuid()).Should().Be(NodeRuntimeStatus.Idle);
    }

    [Test]
    public async Task GetStatus_AfterStart_ConnectedNodeReportsConnected()
    {
        SetupSuccessfulNode();
        var config = SparkplugNode();
        await _service.AddNodeAsync(config);

        await _service.StartAsync();

        _service.GetStatus(config.Id).Should().Be(NodeRuntimeStatus.Connected);
    }

    [Test]
    public async Task GetStatus_NodeFailsToStart_StartAsyncThrows()
    {
        var node = Substitute.For<ISparkplugNode>();
        node.Start(Arg.Any<SparkplugNodeOptions>()).Returns<Task>(_ => throw new Exception("Broker unavailable"));
        _mockNodeFactory.Create(Arg.Any<List<Metric>>(), Arg.Any<SparkplugSpecificationVersion>()).Returns(node);
        var config = SparkplugNode();
        await _service.AddNodeAsync(config);

        // With sequential rollback, StartAsync propagates the runner's exception
        var act = () => _service.StartAsync();
        await act.Should().ThrowAsync<Exception>().WithMessage("*Broker unavailable*");

        _service.IsRunning.Should().BeFalse();
        // Runner was never promoted to _runners, so GetStatus returns Idle
        _service.GetStatus(config.Id).Should().Be(NodeRuntimeStatus.Idle);
    }

    [Test]
    public async Task GetStatus_AfterStop_ReturnsIdle()
    {
        var config = SparkplugNode();
        await _service.AddNodeAsync(config);
        await _service.StartAsync();

        await _service.StopAsync();

        _service.GetStatus(config.Id).Should().Be(NodeRuntimeStatus.Idle);
    }

    [Test]
    public async Task StateChanged_FiresOnStartAndStop()
    {
        await _service.AddNodeAsync(SparkplugNode());
        var fired = 0;
        _service.StateChanged += () => fired++;

        await _service.StartAsync();
        await _service.StopAsync();

        fired.Should().BeGreaterThanOrEqualTo(2);
    }

    [Test]
    public async Task StateChanged_FiresWhenStoreChanges()
    {
        var fired = false;
        _service.StateChanged += () => fired = true;

        await _settingsStore.AddEmulatorNodeAsync(_mockSessionState.SelectedConnection.Id, new EmulatorNodeConfig());

        fired.Should().BeTrue();
    }

    [Test]
    public async Task StartAsync_FiveThousandMetricTick_CompletesWithoutPathologicalCost()
    {
        var config = new EmulatorNodeConfig
        {
            Type = EmulatorNodeType.Generic,
            PayloadFormat = GenericPayloadFormat.PlainText,
            NodeId = "Bulk-1"
        };
        for (var d = 0; d < 10; d++)
        {
            var device = new EmulatorDeviceConfig { DeviceId = $"Device-{d}" };
            for (var m = 0; m < 500; m++)
                device.Metrics.Add(new EmulatorMetricConfig { Name = $"Metric-{m}" });
            config.Devices.Add(device);
        }
        await _service.AddNodeAsync(config);

        var stopwatch = Stopwatch.StartNew();
        await _service.StartAsync();
        stopwatch.Stop();

        stopwatch.ElapsedMilliseconds.Should().BeLessThan(5000);
        await _mockMqttClient.Received(5000).EnqueueAsync(Arg.Any<MqttApplicationMessage>());
    }

    [Test]
    public async Task ResetForConnectionAsync_StopsRunningEmulation()
    {
        await _service.AddNodeAsync(SparkplugNode());
        await _service.StartAsync();
        _service.IsRunning.Should().BeTrue();

        var newConnId = Guid.NewGuid();
        await _service.ResetForConnectionAsync(newConnId);

        _service.IsRunning.Should().BeFalse();
    }

    [Test]
    public async Task ResetForConnectionAsync_SwitchesConnectionId()
    {
        var newConnId = Guid.NewGuid();
        // Add a node config for the new connection in the store
        await _settingsStore.AddEmulatorNodeAsync(newConnId, new EmulatorNodeConfig { NodeId = "NewNode" });

        await _service.ResetForConnectionAsync(newConnId);

        _service.Nodes.Should().HaveCount(1);
        _service.Nodes[0].NodeId.Should().Be("NewNode");
    }

    [Test]
    public async Task ResetForConnectionAsync_WhenNotRunning_SwitchesConnectionIdWithoutError()
    {
        var newConnId = Guid.NewGuid();
        await _settingsStore.AddEmulatorNodeAsync(newConnId, new EmulatorNodeConfig { NodeId = "Node" });

        await _service.ResetForConnectionAsync(newConnId);

        _service.Nodes.Should().HaveCount(1);
        _service.Nodes[0].NodeId.Should().Be("Node");
    }

    [Test]
    public async Task ResetForConnectionAsync_DoesNotAutoStart()
    {
        await _service.AddNodeAsync(SparkplugNode());
        await _service.StartAsync();

        var newConnId = Guid.NewGuid();
        await _settingsStore.AddEmulatorNodeAsync(newConnId, new EmulatorNodeConfig { NodeId = "X" });
        await _service.ResetForConnectionAsync(newConnId);

        _service.IsRunning.Should().BeFalse();
    }

    [Test]
    public async Task ResetForConnectionAsync_FiresStateChanged()
    {
        var fired = false;
        _service.StateChanged += () => fired = true;

        await _service.ResetForConnectionAsync(Guid.NewGuid());

        fired.Should().BeTrue();
    }

    [Test]
    public async Task ResetForConnectionAsync_PreservesSavedNodeConfigs()
    {
        var oldConnId = _mockSessionState.SelectedConnection.Id;
        await _service.AddNodeAsync(SparkplugNode());

        var newConnId = Guid.NewGuid();

        await _service.ResetForConnectionAsync(newConnId);

        // Old connection's saved configs are still in the store
        _settingsStore.GetEmulatorNodes(oldConnId).Should().HaveCount(1);
    }

    [Test]
    public void EmulatorNodeConfig_UseMetricAliases_DefaultsToFalse()
    {
        var config = new EmulatorNodeConfig();
        config.UseMetricAliases.Should().BeFalse();
    }

    [Test]
    public async Task StartAsync_AliasesEnabled_NbirthMetricsHaveNameAndAlias()
    {
        var node = SetupSuccessfulNode();
        List<Metric>? capturedKnownMetrics = null;
        _mockNodeFactory.Create(
                Arg.Any<List<Metric>>(),
                Arg.Any<SparkplugSpecificationVersion>(),
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<Func<string, List<Metric>>>())
            .Returns(ci =>
            {
                capturedKnownMetrics = ci.ArgAt<List<Metric>>(0);
                return node;
            });

        var config = SparkplugNode("Node-1", "Flow Rate");
        config.UseMetricAliases = true;
        await _service.AddNodeAsync(config);

        await _service.StartAsync();

        capturedKnownMetrics.Should().NotBeNull();
        // knownMetrics includes health metrics + Node Control/Rebirth
        // All should have non-null Name (birth mode)
        capturedKnownMetrics!.All(m => !string.IsNullOrEmpty(m.Name)).Should().BeTrue();
        // All should have non-null, non-zero Alias
        capturedKnownMetrics.All(m => m.Alias is not null and not 0).Should().BeTrue();
        // Aliases should be unique
        capturedKnownMetrics.Select(m => m.Alias).Should().OnlyHaveUniqueItems();
        // First alias should be 1
        capturedKnownMetrics.First().Alias.Should().Be(1UL);
    }

    [Test]
    public async Task StartAsync_AliasesEnabled_DbirthMetricsHaveNameAndAlias()
    {
        var node = SetupSuccessfulNode();
        var births = new List<(string DeviceId, List<Metric> Metrics)>();
        node.PublishDeviceBirthMessage(
                Arg.Any<string>(),
                Arg.Any<List<Metric>>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci => births.Add((ci.ArgAt<string>(0), ci.ArgAt<List<Metric>>(1))));

        var config = new EmulatorNodeConfig { Type = EmulatorNodeType.SparkplugB, NodeId = "Node-1" };
        config.Devices.Add(new EmulatorDeviceConfig
        {
            DeviceId = "Flow-1",
            Metrics = [new EmulatorMetricConfig { Name = "Flow Rate" }, new EmulatorMetricConfig { Name = "Pressure" }]
        });
        config.UseMetricAliases = true;
        await _service.AddNodeAsync(config);

        await _service.StartAsync();

        births.Should().ContainSingle(b => b.DeviceId == "Flow-1");
        var metrics = births.Single(b => b.DeviceId == "Flow-1").Metrics;
        // Birth mode: Name is set, Alias is set
        metrics.All(m => !string.IsNullOrEmpty(m.Name)).Should().BeTrue();
        metrics.All(m => m.Alias is not null and not 0).Should().BeTrue();
        metrics.Select(m => m.Alias).Should().OnlyHaveUniqueItems();
        metrics.First().Alias.Should().Be(1UL);
    }

    [Test]
    public async Task StartAsync_AliasesEnabled_NdataMetricsHaveAliasOnly()
    {
        var node = SetupSuccessfulNode();
        List<Metric>? publishedMetrics = null;
        node.PublishMetrics(Arg.Do<List<Metric>>(m => publishedMetrics = m))
            .Returns(Task.CompletedTask);

        var config = SparkplugNode("Node-1");
        config.UseMetricAliases = true;
        await _service.AddNodeAsync(config);

        await _service.StartAsync();

        publishedMetrics.Should().NotBeNull();
        // Data mode: Name is null, Alias is set
        publishedMetrics!.All(m => m.Name == null).Should().BeTrue();
        publishedMetrics.All(m => m.Alias is not null and not 0).Should().BeTrue();
        publishedMetrics.Select(m => m.Alias).Should().OnlyHaveUniqueItems();
    }

    [Test]
    public async Task StartAsync_AliasesEnabled_DdataMetricsHaveAliasOnly()
    {
        var node = SetupSuccessfulNode();
        var deviceMetrics = new Dictionary<string, List<Metric>>();
        node.PublishDeviceMetrics(
            Arg.Any<string>(),
            Arg.Any<List<Metric>>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci => deviceMetrics[ci.ArgAt<string>(0)] = ci.ArgAt<List<Metric>>(1));

        var config = SparkplugNode("Node-1", "Flow Rate", "Pressure");
        config.UseMetricAliases = true;
        await _service.AddNodeAsync(config);

        await _service.StartAsync();

        deviceMetrics.Should().ContainKey("Device-1");
        var metrics = deviceMetrics["Device-1"];
        metrics.All(m => m.Name == null).Should().BeTrue();
        metrics.All(m => m.Alias is not null and not 0).Should().BeTrue();
        metrics.Select(m => m.Alias).Should().OnlyHaveUniqueItems();
        metrics.First().Alias.Should().Be(1UL);
    }

    [Test]
    public async Task StartAsync_AliasesDisabled_MetricsHaveNameOnlyNoAlias()
    {
        var node = SetupSuccessfulNode();
        List<Metric>? publishedMetrics = null;
        node.PublishMetrics(Arg.Do<List<Metric>>(m => publishedMetrics = m))
            .Returns(Task.CompletedTask);

        var config = SparkplugNode("Node-1");
        config.UseMetricAliases = false;
        await _service.AddNodeAsync(config);

        await _service.StartAsync();

        publishedMetrics.Should().NotBeNull();
        publishedMetrics!.All(m => !string.IsNullOrEmpty(m.Name)).Should().BeTrue();
        publishedMetrics.All(m => m.Alias == null).Should().BeTrue();
    }

    [Test]
    public async Task StartAsync_AliasesDisabled_NbirthMetricsHaveNameOnlyNoAlias()
    {
        var node = SetupSuccessfulNode();
        List<Metric>? capturedKnownMetrics = null;
        _mockNodeFactory.Create(
            Arg.Do<List<Metric>>(m => capturedKnownMetrics = m),
            Arg.Any<SparkplugSpecificationVersion>()).Returns(node);

        var config = SparkplugNode("Node-1", "Flow Rate");
        config.UseMetricAliases = false;
        await _service.AddNodeAsync(config);

        await _service.StartAsync();

        capturedKnownMetrics.Should().NotBeNull();
        capturedKnownMetrics!.All(m => !string.IsNullOrEmpty(m.Name)).Should().BeTrue();
        capturedKnownMetrics.All(m => m.Alias == null).Should().BeTrue();
    }

    [Test]
    public async Task StartAsync_AliasesDisabled_DbirthMetricsHaveNameOnlyNoAlias()
    {
        var node = SetupSuccessfulNode();
        var births = new List<(string DeviceId, List<Metric> Metrics)>();
        node.PublishDeviceBirthMessage(
                Arg.Any<string>(),
                Arg.Any<List<Metric>>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci => births.Add((ci.ArgAt<string>(0), ci.ArgAt<List<Metric>>(1))));

        var config = SparkplugNode("Node-1", "Flow Rate");
        config.UseMetricAliases = false;
        await _service.AddNodeAsync(config);

        await _service.StartAsync();

        births.Should().ContainSingle();
        var metrics = births[0].Metrics;
        metrics.All(m => !string.IsNullOrEmpty(m.Name)).Should().BeTrue();
        metrics.All(m => m.Alias == null).Should().BeTrue();
    }

    [Test]
    public async Task StartAsync_AliasesEnabled_AreDeterministicAcrossRuns()
    {
        // Run 1 — capture NDATA aliases
        var node1 = SetupSuccessfulNode();
        List<Metric>? run1Metrics = null;
        node1.PublishMetrics(Arg.Do<List<Metric>>(m => run1Metrics = m))
            .Returns(Task.CompletedTask);
        var config1 = SparkplugNode("Node-1", "Flow Rate");
        config1.UseMetricAliases = true;
        await _service.AddNodeAsync(config1);
        await _service.StartAsync();
        var run1Aliases = run1Metrics!.Select(m => m.Alias).ToList();
        await _service.StopAsync();
        await _service.RemoveAllNodesAsync();

        // Run 2 — identical config, same service instance, nodes cleared
        var node2 = SetupSuccessfulNode();
        List<Metric>? run2Metrics = null;
        node2.PublishMetrics(Arg.Do<List<Metric>>(m => run2Metrics = m))
            .Returns(Task.CompletedTask);
        var config2 = SparkplugNode("Node-1", "Flow Rate");
        config2.UseMetricAliases = true;
        await _service.AddNodeAsync(config2);
        await _service.StartAsync();
        var run2Aliases = run2Metrics!.Select(m => m.Alias).ToList();

        // Precondition: aliases must be set (not null) before comparing determinism
        run1Aliases.Should().AllSatisfy(a => a.Should().NotBeNull());
        run2Aliases.Should().AllSatisfy(a => a.Should().NotBeNull());
        run1Aliases.Should().Equal(run2Aliases);
    }

    [Test]
    public async Task StartAsync_AliasesEnabled_MultipleDevicesHaveIndependentAliasSequences()
    {
        var node = SetupSuccessfulNode();
        var deviceMetrics = new Dictionary<string, List<Metric>>();
        node.PublishDeviceMetrics(
            Arg.Any<string>(),
            Arg.Any<List<Metric>>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci => deviceMetrics[ci.ArgAt<string>(0)] = ci.ArgAt<List<Metric>>(1));

        var config = new EmulatorNodeConfig { Type = EmulatorNodeType.SparkplugB, NodeId = "Node-1" };
        config.Devices.Add(new EmulatorDeviceConfig
        {
            DeviceId = "Flow-1",
            Metrics =
            [
                new EmulatorMetricConfig { Name = "Flow Rate" },
                new EmulatorMetricConfig { Name = "Pressure" },
                new EmulatorMetricConfig { Name = "Temperature" }
            ]
        });
        config.Devices.Add(new EmulatorDeviceConfig
        {
            DeviceId = "Valve-1",
            Metrics = [new EmulatorMetricConfig { Name = "Position" }]
        });
        config.UseMetricAliases = true;
        await _service.AddNodeAsync(config);

        await _service.StartAsync();

        deviceMetrics.Should().ContainKey("Flow-1");
        deviceMetrics.Should().ContainKey("Valve-1");

        // Flow-1 has 3 metrics: aliases 1, 2, 3
        var flowMetrics = deviceMetrics["Flow-1"];
        flowMetrics.Should().HaveCount(3);
        flowMetrics.Select(m => m.Alias).Should().BeEquivalentTo(new ulong?[] { 1, 2, 3 });

        // Valve-1 has 1 metric: alias 1 (independent sequence)
        var valveMetrics = deviceMetrics["Valve-1"];
        valveMetrics.Should().HaveCount(1);
        valveMetrics.Single().Alias.Should().Be(1UL);
    }

    [Test]
    public async Task StartAsync_AliasesEnabled_AliasZeroNeverAssigned()
    {
        var node = SetupSuccessfulNode();
        List<Metric>? capturedKnownMetrics = null;
        _mockNodeFactory.Create(
                Arg.Any<List<Metric>>(),
                Arg.Any<SparkplugSpecificationVersion>(),
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<Func<string, List<Metric>>>())
            .Returns(ci =>
            {
                capturedKnownMetrics = ci.ArgAt<List<Metric>>(0);
                return node;
            });
        List<Metric>? publishedMetrics = null;
        node.PublishMetrics(Arg.Do<List<Metric>>(m => publishedMetrics = m))
            .Returns(Task.CompletedTask);
        var deviceMetrics = new Dictionary<string, List<Metric>>();
        node.PublishDeviceMetrics(
            Arg.Any<string>(),
            Arg.Any<List<Metric>>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci => deviceMetrics[ci.ArgAt<string>(0)] = ci.ArgAt<List<Metric>>(1));

        var config = SparkplugNode("Node-1", "Flow Rate");
        config.UseMetricAliases = true;
        await _service.AddNodeAsync(config);

        await _service.StartAsync();

        // Alias 0 is reserved; all aliases must be non-null and non-zero
        capturedKnownMetrics!.All(m => m.Alias is not null and not 0).Should().BeTrue();
        publishedMetrics!.All(m => m.Alias is not null and not 0).Should().BeTrue();
        deviceMetrics["Device-1"].All(m => m.Alias is not null and not 0).Should().BeTrue();
    }

    [Test]
    public async Task Rebirth_AliasesEnabled_KnownMetricsPassedToFactoryHaveAliases()
    {
        var node = SetupSuccessfulNode();
        // Use Returns callback to capture metrics from the 4-param Create overload
        List<Metric>? capturedKnownMetrics = null;
        _mockNodeFactory.Create(
                Arg.Any<List<Metric>>(),
                Arg.Any<SparkplugSpecificationVersion>(),
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<Func<string, List<Metric>>>())
            .Returns(ci =>
            {
                capturedKnownMetrics = ci.ArgAt<List<Metric>>(0);
                return node;
            });

        var config = SparkplugNode("Node-1", "Flow Rate");
        config.UseMetricAliases = true;
        await _service.AddNodeAsync(config);
        await _service.StartAsync();

        capturedKnownMetrics.Should().NotBeNull();
        // All known metrics (health + Node Control/Rebirth) must carry aliases
        // so that SparkplugNet's Rebirth method re-publishes NBIRTH with aliases.
        capturedKnownMetrics!.All(m => m.Alias is not null and not 0).Should().BeTrue();
        capturedKnownMetrics.All(m => !string.IsNullOrEmpty(m.Name)).Should().BeTrue();
        capturedKnownMetrics.Select(m => m.Alias).Should().OnlyHaveUniqueItems();
    }

    [Test]
    public async Task Rebirth_AliasesEnabled_DbirthRepublishedWithAliases()
    {
        var node = SetupSuccessfulNode();
        var birthCalls = new List<(string DeviceId, List<Metric> Metrics)>();
        node.PublishDeviceBirthMessage(Arg.Any<string>(), Arg.Any<List<Metric>>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci => birthCalls.Add((ci.ArgAt<string>(0), ci.ArgAt<List<Metric>>(1))));

        var config = SparkplugNode("Node-1", "Flow Rate");
        config.UseMetricAliases = true;
        await _service.AddNodeAsync(config);
        await _service.StartAsync();

        // Initial DBIRTH during Start
        birthCalls.Should().ContainSingle();
        birthCalls[0].Metrics.All(m => m.Alias is not null and not 0).Should().BeTrue();

        // The adapter is wired to republish DBIRTH on rebirth.
        // Verify the factory was created with device-aware parameters by checking
        // that the Create call included the device callback signature.
        // The ISparkplugNodeFactory.Create overload with getDeviceBirthMetrics is called.
        _mockNodeFactory.Received(1).Create(
            Arg.Any<List<Metric>>(),
            Arg.Any<SparkplugSpecificationVersion>(),
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<Func<string, List<Metric>>>());
    }

    [Test]
    public async Task Rebirth_AliasesDisabled_NoDeviceCallbackPassed()
    {
        var node = SetupSuccessfulNode();

        var config = SparkplugNode("Node-1", "Flow Rate");
        config.UseMetricAliases = false;
        await _service.AddNodeAsync(config);
        await _service.StartAsync();

        // When aliases are disabled, the 2-parameter Create overload is used
        // (no device callback is needed).
        _mockNodeFactory.Received(1).Create(
            Arg.Any<List<Metric>>(),
            Arg.Any<SparkplugSpecificationVersion>());
        // The 4-parameter overload must NOT be called.
        _mockNodeFactory.DidNotReceive().Create(
            Arg.Any<List<Metric>>(),
            Arg.Any<SparkplugSpecificationVersion>(),
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<Func<string, List<Metric>>>());
    }

    [Test]
    public void SparkplugNet_Metric_AliasIsSettableAndSerializes()
    {
        // SparkplugNet uses an internal PayloadConverter → ProtoBufPayload → PayloadHelper
        // pipeline for serialization. ProtoBuf.Serializer.Serialize does not work directly
        // with the public Metric type (no protobuf-net contract). We exercise the real path.
        var metric = new Metric("Test", DataType.Double, 42.0);
        metric.Alias = 7;

        metric.Alias.Should().Be(7UL);

        var bytes = SerializeMetricThroughPayload(metric);
        var roundtripped = DeserializePayloadToMetric(bytes);

        roundtripped.Name.Should().Be("Test");
        roundtripped.Alias.Should().Be(7UL);
    }

    [Test]
    public void SparkplugNet_Metric_NameNull_OmitsFieldInSerialization()
    {
        // The SparkplugB spec requires NDATA/DDATA to carry alias only, with no name.
        // Metric.Name = null must cause the name field to be absent from the serialized
        // payload — not present as an empty string.
        var metric = new Metric("Test", DataType.Double, 42.0);
        metric.Alias = 7;
        metric.Name = null!; // Data mode: alias-only

        var bytesWithName = SerializeMetricThroughPayload(new Metric("Test", DataType.Double, 42.0) { Alias = 7 });
        var bytesNullName = SerializeMetricThroughPayload(metric);

        // Null name must serialize to fewer bytes than a set name (field omitted).
        bytesNullName.Length.Should().BeLessThan(bytesWithName.Length,
            "Name=null must omit the name field from the serialized payload");

        // Round-trip: alias must survive.
        var roundtripped = DeserializePayloadToMetric(bytesNullName);
        roundtripped.Alias.Should().Be(7UL);
    }

    /// <summary>
    /// Serializes a single <see cref="Metric"/> through SparkplugNet's internal
    /// Payload → ProtoBufPayload → byte[] pipeline using reflection.
    /// </summary>
    private static byte[] SerializeMetricThroughPayload(Metric metric)
    {
        var payload = new Payload();
        payload.Metrics.Add(metric);

        var sparkplugAsm = typeof(Metric).Assembly;
        var converterType = sparkplugAsm.GetType("SparkplugNet.VersionB.PayloadConverter")!;
        var protoPayloadType = sparkplugAsm.GetType("SparkplugNet.VersionB.ProtoBuf.ProtoBufPayload")!;
        var helperType = sparkplugAsm.GetType("SparkplugNet.Core.PayloadHelper")!;

        var convertMethod = converterType.GetMethod("ConvertVersionBPayload",
            BindingFlags.Public | BindingFlags.Static,
            null, new[] { typeof(Payload) }, null)!;
        var protoPayload = convertMethod.Invoke(null, new object[] { payload })!;

        var serializeMethod = helperType
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .First(m => m.Name == "Serialize" && m.GetParameters().Length == 1);
        var genericSerialize = serializeMethod.MakeGenericMethod(protoPayloadType);

        return (byte[])genericSerialize.Invoke(null, new object[] { protoPayload })!;
    }

    /// <summary>
    /// Deserializes bytes through SparkplugNet's internal pipeline and returns
    /// the first <see cref="Metric"/> from the resulting payload.
    /// </summary>
    private static Metric DeserializePayloadToMetric(byte[] data)
    {
        var sparkplugAsm = typeof(Metric).Assembly;
        var converterType = sparkplugAsm.GetType("SparkplugNet.VersionB.PayloadConverter")!;
        var protoPayloadType = sparkplugAsm.GetType("SparkplugNet.VersionB.ProtoBuf.ProtoBufPayload")!;
        var helperType = sparkplugAsm.GetType("SparkplugNet.Core.PayloadHelper")!;

        var deserializeMethod = helperType
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .First(m => m.Name == "Deserialize" && m.GetParameters().Length == 1);
        var genericDeserialize = deserializeMethod.MakeGenericMethod(protoPayloadType);
        var protoPayload = genericDeserialize.Invoke(null, new object[] { data })!;

        var convertMethod = converterType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(m => m.Name == "ConvertVersionBPayload"
                        && m.GetParameters()[0].ParameterType != typeof(Payload));
        var payload = (Payload)convertMethod.Invoke(null, new object[] { protoPayload })!;

        return payload.Metrics[0];
    }

    [Test]
    public async Task StartAsync_FirstTick_CallsUpdateEmulatorHealth()
    {
        await _service.AddNodeAsync(SparkplugNode("node-01"));
        await _service.StartAsync();

        _mockMetrics.Received(1).UpdateEmulatorHealth(
            Arg.Is<int>(n => n >= 0),
            Arg.Is<long>(n => n >= 0),
            Arg.Is<int>(n => n >= 0));
    }

    [Test]
    public async Task StopAsync_CallsClearEmulatorHealth()
    {
        await _service.AddNodeAsync(SparkplugNode("node-01"));
        await _service.StartAsync();
        await _service.StopAsync();

        _mockMetrics.Received(1).ClearEmulatorHealth();
    }
}
