using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;
using MqttProbe.Models.Emulation;
using MqttProbe.Models.Mqtt;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Emulation;
using MqttProbe.Services.Mqtt;
using MqttProbe.Services.Sparkplug;
using SparkplugNet.Core.Enumerations;
using SparkplugNet.Core.Node;
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
    private EmulationService _service = null!;
    private Func<MqttClientDisconnectedEventArgs, Task>? _disconnectedHandler;

    [SetUp]
    public async Task Setup()
    {
        _filePath = Path.Combine(Path.GetTempPath(), $"emulation_service_test_{Guid.NewGuid()}.json");
        var settingsStore = new SettingsStore(_filePath);
        _settingsStore = settingsStore;
        await settingsStore.LoadAsync();
        // A long interval keeps the background loop from ticking during a test,
        // so the single inline tick from StartAsync is the only publish observed.
        await settingsStore.SetEmulatorPublishIntervalAsync(120_000);

        _mockSessionState = Substitute.For<ISessionState>();
        _mockSessionState.SelectedConnection.Returns(new Connection());
        _mockNodeFactory = Substitute.For<ISparkplugNodeFactory>();
        _mockMqttClient = Substitute.For<IManagedMqttClient>();
        _mockMqttClient.EnqueueAsync(Arg.Any<MqttApplicationMessage>()).Returns(Task.CompletedTask);

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
            Substitute.For<ILogger<EmulationService>>());
    }

    [TearDown]
    public async Task TearDown()
    {
        await _service.StopAsync();
        _service.Dispose();
        _mockMqttClient.Dispose();
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
        _settingsStore.EmulatorNodes.Should().HaveCount(1);
    }

    [Test]
    public async Task SetPublishIntervalAsync_DelegatesToStore()
    {
        await _service.SetPublishIntervalAsync(750);

        _service.PublishIntervalMs.Should().Be(750);
        _settingsStore.EmulatorPublishIntervalMs.Should().Be(750);
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
    public async Task GetStatus_NodeFailsToStart_ReportsError()
    {
        var node = Substitute.For<ISparkplugNode>();
        node.Start(Arg.Any<SparkplugNodeOptions>()).Returns<Task>(_ => throw new Exception("Broker unavailable"));
        _mockNodeFactory.Create(Arg.Any<List<Metric>>(), Arg.Any<SparkplugSpecificationVersion>()).Returns(node);
        var config = SparkplugNode();
        await _service.AddNodeAsync(config);

        await _service.StartAsync();

        _service.GetStatus(config.Id).Should().Be(NodeRuntimeStatus.Error);
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

        await _settingsStore.AddEmulatorNodeAsync(new EmulatorNodeConfig());

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
}
