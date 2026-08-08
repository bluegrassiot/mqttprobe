using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MQTTnet;
using MQTTnet.Protocol;
using MqttProbe.Models.Configuration;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Mqtt;
using MqttProbe.Services.Plugins.Contracts;
using MqttProbe.Services.Sparkplug;

namespace MqttProbe.Shared.Tests.Services.Sparkplug;

[TestFixture]
public class SparkplugCommandServiceTests
{
    private IMqttManagedClient _mockClient = null!;
    private ILogger<SparkplugCommandService> _mockLogger = null!;
    private IUiSettings _uiSettings = null!;
    private SparkplugTopologyService _topology = null!;
    private SparkplugCommandService _service = null!;
    private FakeTimeProvider _fakeClock = null!;

    [SetUp]
    public void Setup()
    {
        _mockClient = Substitute.For<IMqttManagedClient>();
        _mockLogger = Substitute.For<ILogger<SparkplugCommandService>>();
        _uiSettings = MakeUiSettings(autoRequestRebirth: false, allowNodeReboot: false);
        _topology = new SparkplugTopologyService();
        _fakeClock = new FakeTimeProvider();
        _service = new SparkplugCommandService(_mockClient, _mockLogger, _uiSettings, _topology, _fakeClock);
    }

    [TearDown]
    public void TearDown()
    {
        _mockClient.Dispose();
    }

    private static IUiSettings MakeUiSettings(bool autoRequestRebirth, bool allowNodeReboot)
    {
        var store = Substitute.For<IUiSettings>();
        var config = new AppConfiguration
        {
            Ui = new UiPreferences
            {
                AutoRequestSparkplugRebirth = autoRequestRebirth,
                AllowNodeReboot = allowNodeReboot
            }
        };
        store.Ui.Returns(config.Ui);
        return store;
    }

    private static MetricSnapshot Ms(string name, string dataType = "double", string value = "1.0000") =>
        new() { Name = name, DataType = dataType, Value = value };

    private async Task SeedNodeBirth(string group = "g", string node = "n")
    {
        await _topology.ApplyTopologyEventsAsync(
        [
            new NodeBirthEvent
            {
                FormatId = "sparkplug-b",
                Topic = $"spBv1.0/{group}/NBIRTH/{node}",
                GroupId = group,
                NodeId = node,
                Metrics = [Ms("Temp")]
            }
        ]);
    }

    private async Task SeedNodeData(string group = "g", string node = "n")
    {
        await _topology.ApplyTopologyEventsAsync(
        [
            new NodeDataEvent
            {
                FormatId = "sparkplug-b",
                Topic = $"spBv1.0/{group}/NDATA/{node}",
                GroupId = group,
                NodeId = node,
                Metrics = [Ms("Temp", "double", "25.0000")]
            }
        ]);
    }

    private async Task SeedDeviceBirth(string group = "g", string node = "n", string device = "d1")
    {
        await _topology.ApplyTopologyEventsAsync(
        [
            new NodeBirthEvent
            {
                FormatId = "sparkplug-b",
                Topic = $"spBv1.0/{group}/NBIRTH/{node}",
                GroupId = group,
                NodeId = node,
                Metrics = []
            },
            new DeviceBirthEvent
            {
                FormatId = "sparkplug-b",
                Topic = $"spBv1.0/{group}/DBIRTH/{node}/{device}",
                GroupId = group,
                NodeId = node,
                DeviceId = device,
                Metrics = [Ms("Voltage")]
            }
        ]);
    }

    // --- Node rebirth ---

    [Test]
    public async Task RequestNodeRebirth_PublishesCorrectTopicAndMetric()
    {
        await SeedNodeBirth();

        var result = await _service.RequestNodeRebirthAsync("g", "n");

        result.Should().Be(SparkplugCommandResult.Published);
        await _mockClient.Received(1).EnqueueAsync(
            Arg.Is<MqttApplicationMessage>(m =>
                m!.Topic == "spBv1.0/g/NCMD/n"
                && m.QualityOfServiceLevel == MqttQualityOfServiceLevel.AtLeastOnce));
    }

    [Test]
    public async Task RequestNodeRebirth_PayloadContainsNodeControlRebirth()
    {
        await SeedNodeBirth();

        await _service.RequestNodeRebirthAsync("g", "n");

        await _mockClient.Received(1).EnqueueAsync(
            Arg.Is<MqttApplicationMessage>(m => VerifyMetricPayload(m!, "Node Control/Rebirth")));
    }

    [Test]
    public async Task RequestNodeRebirth_SetsLastRebirthRequestAt()
    {
        await SeedNodeBirth();

        await _service.RequestNodeRebirthAsync("g", "n");

        var node = _topology.Groups["g"].Nodes["n"];
        node.LastRebirthRequestAt.Should().NotBeNull();
        node.LastRebirthRequestAt.Should().Be(_fakeClock.GetUtcNow().UtcDateTime);
    }

    [Test]
    public async Task RequestNodeRebirth_NonExistentGroup_ReturnsTargetNotFound()
    {
        var result = await _service.RequestNodeRebirthAsync("missing", "n");

        result.Should().Be(SparkplugCommandResult.TargetNotFound);
        await _mockClient.DidNotReceive().EnqueueAsync(Arg.Any<MqttApplicationMessage>());
    }

    [Test]
    public async Task RequestNodeRebirth_NonExistentNode_ReturnsTargetNotFound()
    {
        await SeedNodeBirth();

        var result = await _service.RequestNodeRebirthAsync("g", "missing");

        result.Should().Be(SparkplugCommandResult.TargetNotFound);
        await _mockClient.DidNotReceive().EnqueueAsync(Arg.Any<MqttApplicationMessage>());
    }

    [Test]
    public async Task RequestNodeRebirth_ManualWorksWhenAutoDisabled()
    {
        _uiSettings = MakeUiSettings(autoRequestRebirth: false, allowNodeReboot: false);
        _service = new SparkplugCommandService(_mockClient, _mockLogger, _uiSettings, _topology, _fakeClock);
        await SeedNodeBirth();

        var result = await _service.RequestNodeRebirthAsync("g", "n");

        result.Should().Be(SparkplugCommandResult.Published);
    }

    [Test]
    public async Task RequestNodeRebirth_PublishFails_ReturnsFailed()
    {
        await SeedNodeBirth();
        _mockClient.EnqueueAsync(Arg.Any<MqttApplicationMessage>())
            .Returns(Task.FromException(new InvalidOperationException("connection lost")));

        var result = await _service.RequestNodeRebirthAsync("g", "n");

        result.Should().Be(SparkplugCommandResult.Failed);
    }

    // --- Device rebirth ---

    [Test]
    public async Task RequestDeviceRebirth_PublishesDcmdTopic()
    {
        await SeedDeviceBirth();

        var result = await _service.RequestDeviceRebirthAsync("g", "n", "d1");

        result.Should().Be(SparkplugCommandResult.Published);
        await _mockClient.Received(1).EnqueueAsync(
            Arg.Is<MqttApplicationMessage>(m =>
                m!.Topic == "spBv1.0/g/DCMD/n/d1"
                && m.QualityOfServiceLevel == MqttQualityOfServiceLevel.AtLeastOnce));
    }

    [Test]
    public async Task RequestDeviceRebirth_PayloadContainsDeviceControlRebirth()
    {
        await SeedDeviceBirth();

        await _service.RequestDeviceRebirthAsync("g", "n", "d1");

        await _mockClient.Received(1).EnqueueAsync(
            Arg.Is<MqttApplicationMessage>(m => VerifyMetricPayload(m!, "Device Control/Rebirth")));
    }

    [Test]
    public async Task RequestDeviceRebirth_NonExistentDevice_ReturnsTargetNotFound()
    {
        await SeedNodeBirth();

        var result = await _service.RequestDeviceRebirthAsync("g", "n", "missing-device");

        result.Should().Be(SparkplugCommandResult.TargetNotFound);
        await _mockClient.DidNotReceive().EnqueueAsync(Arg.Any<MqttApplicationMessage>());
    }

    [Test]
    public async Task RequestDeviceRebirth_NonExistentNode_ReturnsTargetNotFound()
    {
        var result = await _service.RequestDeviceRebirthAsync("g", "missing", "d1");

        result.Should().Be(SparkplugCommandResult.TargetNotFound);
    }

    [Test]
    public async Task RequestDeviceRebirth_DoesNotSetLastRebirthRequestAt()
    {
        await SeedDeviceBirth();

        await _service.RequestDeviceRebirthAsync("g", "n", "d1");

        var node = _topology.Groups["g"].Nodes["n"];
        node.LastRebirthRequestAt.Should().BeNull();
    }

    // --- Node reboot ---

    [Test]
    public async Task RequestNodeReboot_WhenAllowed_PublishesNcmdTopic()
    {
        _uiSettings = MakeUiSettings(autoRequestRebirth: false, allowNodeReboot: true);
        _service = new SparkplugCommandService(_mockClient, _mockLogger, _uiSettings, _topology, _fakeClock);
        await SeedNodeBirth();

        var result = await _service.RequestNodeRebootAsync("g", "n");

        result.Should().Be(SparkplugCommandResult.Published);
        await _mockClient.Received(1).EnqueueAsync(
            Arg.Is<MqttApplicationMessage>(m =>
                m!.Topic == "spBv1.0/g/NCMD/n"
                && m.QualityOfServiceLevel == MqttQualityOfServiceLevel.AtLeastOnce));
    }

    [Test]
    public async Task RequestNodeReboot_WhenAllowed_PayloadContainsNodeControlReboot()
    {
        _uiSettings = MakeUiSettings(autoRequestRebirth: false, allowNodeReboot: true);
        _service = new SparkplugCommandService(_mockClient, _mockLogger, _uiSettings, _topology, _fakeClock);
        await SeedNodeBirth();

        await _service.RequestNodeRebootAsync("g", "n");

        await _mockClient.Received(1).EnqueueAsync(
            Arg.Is<MqttApplicationMessage>(m => VerifyMetricPayload(m!, "Node Control/Reboot")));
    }

    [Test]
    public async Task RequestNodeReboot_WhenNotAllowed_ReturnsNotAllowedAndDoesNotPublish()
    {
        // default: allowNodeReboot = false
        await SeedNodeBirth();

        var result = await _service.RequestNodeRebootAsync("g", "n");

        result.Should().Be(SparkplugCommandResult.NotAllowed);
        await _mockClient.DidNotReceive().EnqueueAsync(Arg.Any<MqttApplicationMessage>());
    }

    [Test]
    public async Task RequestNodeReboot_NonExistentNode_ReturnsTargetNotFound()
    {
        _uiSettings = MakeUiSettings(autoRequestRebirth: false, allowNodeReboot: true);
        _service = new SparkplugCommandService(_mockClient, _mockLogger, _uiSettings, _topology, _fakeClock);

        var result = await _service.RequestNodeRebootAsync("g", "missing");

        result.Should().Be(SparkplugCommandResult.TargetNotFound);
    }

    // --- Auto rebirth ---

    [Test]
    public async Task RequestNodeRebirthIfNeeded_AutoOn_NodeNotOnline_PublishesRebirth()
    {
        _uiSettings = MakeUiSettings(autoRequestRebirth: true, allowNodeReboot: false);
        _service = new SparkplugCommandService(_mockClient, _mockLogger, _uiSettings, _topology, _fakeClock);
        await SeedNodeData(); // creates node with Unknown status

        await _service.RequestNodeRebirthIfNeededAsync("g", "n");

        await _mockClient.Received(1).EnqueueAsync(
            Arg.Is<MqttApplicationMessage>(m => m!.Topic == "spBv1.0/g/NCMD/n"));
    }

    [Test]
    public async Task RequestNodeRebirthIfNeeded_AutoOff_DoesNotPublish()
    {
        _uiSettings = MakeUiSettings(autoRequestRebirth: false, allowNodeReboot: false);
        _service = new SparkplugCommandService(_mockClient, _mockLogger, _uiSettings, _topology, _fakeClock);
        await SeedNodeData();

        await _service.RequestNodeRebirthIfNeededAsync("g", "n");

        await _mockClient.DidNotReceive().EnqueueAsync(Arg.Any<MqttApplicationMessage>());
    }

    [Test]
    public async Task RequestNodeRebirthIfNeeded_NodeOnline_DoesNotPublish()
    {
        _uiSettings = MakeUiSettings(autoRequestRebirth: true, allowNodeReboot: false);
        _service = new SparkplugCommandService(_mockClient, _mockLogger, _uiSettings, _topology, _fakeClock);
        await SeedNodeBirth(); // status = Online

        await _service.RequestNodeRebirthIfNeededAsync("g", "n");

        await _mockClient.DidNotReceive().EnqueueAsync(Arg.Any<MqttApplicationMessage>());
    }

    [Test]
    public async Task RequestNodeRebirthIfNeeded_CooldownNotExpired_DoesNotPublishAgain()
    {
        _uiSettings = MakeUiSettings(autoRequestRebirth: true, allowNodeReboot: false);
        _service = new SparkplugCommandService(_mockClient, _mockLogger, _uiSettings, _topology, _fakeClock);
        await SeedNodeData();

        await _service.RequestNodeRebirthIfNeededAsync("g", "n");
        await _mockClient.Received(1).EnqueueAsync(Arg.Any<MqttApplicationMessage>());

        // Second call within cooldown
        await _service.RequestNodeRebirthIfNeededAsync("g", "n");
        await _mockClient.Received(1).EnqueueAsync(Arg.Any<MqttApplicationMessage>());
    }

    [Test]
    public async Task RequestNodeRebirthIfNeeded_CooldownExpired_PublishesAgain()
    {
        _uiSettings = MakeUiSettings(autoRequestRebirth: true, allowNodeReboot: false);
        _service = new SparkplugCommandService(_mockClient, _mockLogger, _uiSettings, _topology, _fakeClock);
        await SeedNodeData();

        await _service.RequestNodeRebirthIfNeededAsync("g", "n");
        await _mockClient.Received(1).EnqueueAsync(Arg.Any<MqttApplicationMessage>());

        _fakeClock.Advance(TimeSpan.FromSeconds(31));

        await _service.RequestNodeRebirthIfNeededAsync("g", "n");
        await _mockClient.Received(2).EnqueueAsync(
            Arg.Is<MqttApplicationMessage>(m => m!.Topic == "spBv1.0/g/NCMD/n"));
    }

    [Test]
    public async Task RequestNodeRebirthIfNeeded_SetsLastRebirthRequestAt()
    {
        _uiSettings = MakeUiSettings(autoRequestRebirth: true, allowNodeReboot: false);
        _service = new SparkplugCommandService(_mockClient, _mockLogger, _uiSettings, _topology, _fakeClock);
        await SeedNodeData();

        await _service.RequestNodeRebirthIfNeededAsync("g", "n");

        var node = _topology.Groups["g"].Nodes["n"];
        node.LastRebirthRequestAt.Should().NotBeNull();
    }

    [Test]
    public async Task RequestNodeRebirthIfNeeded_NonExistentNode_DoesNotThrow()
    {
        _uiSettings = MakeUiSettings(autoRequestRebirth: true, allowNodeReboot: false);
        _service = new SparkplugCommandService(_mockClient, _mockLogger, _uiSettings, _topology, _fakeClock);

        var act = () => _service.RequestNodeRebirthIfNeededAsync("g", "missing");

        await act.Should().NotThrowAsync();
    }

    [Test]
    public async Task RequestNodeRebirthIfNeeded_PublishFails_DoesNotThrow()
    {
        _uiSettings = MakeUiSettings(autoRequestRebirth: true, allowNodeReboot: false);
        _service = new SparkplugCommandService(_mockClient, _mockLogger, _uiSettings, _topology, _fakeClock);
        await SeedNodeData();
        _mockClient.EnqueueAsync(Arg.Any<MqttApplicationMessage>())
            .Returns(Task.FromException(new InvalidOperationException("connection lost")));

        var act = () => _service.RequestNodeRebirthIfNeededAsync("g", "n");

        await act.Should().NotThrowAsync();
    }

    [Test]
    public async Task RequestNodeRebirthIfNeeded_ConcurrentCalls_OnlyOnePublish()
    {
        _uiSettings = MakeUiSettings(autoRequestRebirth: true, allowNodeReboot: false);
        _service = new SparkplugCommandService(_mockClient, _mockLogger, _uiSettings, _topology, _fakeClock);
        await SeedNodeData();

        var tasks = Enumerable.Range(0, 10)
            .Select(_ => _service.RequestNodeRebirthIfNeededAsync("g", "n"));

        await Task.WhenAll(tasks);

        await _mockClient.Received(1).EnqueueAsync(
            Arg.Is<MqttApplicationMessage>(m => m!.Topic == "spBv1.0/g/NCMD/n"));
    }

    [Test]
    public async Task RequestNodeRebirthIfNeeded_SetsLastRebirthRequestAtBeforePublish()
    {
        _uiSettings = MakeUiSettings(autoRequestRebirth: true, allowNodeReboot: false);
        _service = new SparkplugCommandService(_mockClient, _mockLogger, _uiSettings, _topology, _fakeClock);
        await SeedNodeData();

        await _service.RequestNodeRebirthIfNeededAsync("g", "n");

        var node = _topology.Groups["g"].Nodes["n"];
        node.LastRebirthRequestAt.Should().Be(_fakeClock.GetUtcNow().UtcDateTime);
    }

    [Test]
    public async Task RequestNodeRebirthIfNeeded_OnlyPublishesNodeControlRebirth()
    {
        _uiSettings = MakeUiSettings(autoRequestRebirth: true, allowNodeReboot: false);
        _service = new SparkplugCommandService(_mockClient, _mockLogger, _uiSettings, _topology, _fakeClock);
        await SeedNodeData();

        await _service.RequestNodeRebirthIfNeededAsync("g", "n");

        await _mockClient.Received(1).EnqueueAsync(
            Arg.Is<MqttApplicationMessage>(m =>
                m!.Topic == "spBv1.0/g/NCMD/n"
                && VerifyMetricPayload(m, "Node Control/Rebirth")));
        // Ensure no DCMD (device rebirth) or reboot metric was published
        await _mockClient.DidNotReceive().EnqueueAsync(
            Arg.Is<MqttApplicationMessage>(m => m!.Topic.Contains("/DCMD/")));
        await _mockClient.DidNotReceive().EnqueueAsync(
            Arg.Is<MqttApplicationMessage>(m => VerifyMetricPayload(m!, "Node Control/Reboot")));
    }

    // --- Helpers ---

    private static bool VerifyMetricPayload(MqttApplicationMessage message, string expectedMetricName)
    {
        var payload = Org.Eclipse.Tahu.Protobuf.Payload.Parser.ParseFrom(
            message.GetPayloadSegment().ToArray());
        return payload.Metrics.Count == 1
            && payload.Metrics[0].Name == expectedMetricName
            && payload.Metrics[0].Datatype == 11
            && payload.Metrics[0].BooleanValue;
    }
}
