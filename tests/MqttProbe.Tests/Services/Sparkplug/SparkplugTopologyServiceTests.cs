using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MQTTnet;
using MQTTnet.Protocol;
using MqttProbe.Models.Sparkplug;
using MqttProbe.Services.Mqtt;
using MqttProbe.Services.Plugins.Contracts;
using MqttProbe.Services.Sparkplug;
using Org.Eclipse.Tahu.Protobuf;

namespace MqttProbe.Shared.Tests.Services.Sparkplug;

[TestFixture]
public class SparkplugTopologyServiceTests
{
    private IMqttManagedClient _mockClient = null!;
    private ILogger<SparkplugTopologyService> _mockLogger = null!;
    private SparkplugTopologyService _service = null!;
    private Func<MqttApplicationMessageReceivedEventArgs, Task>? _handler;

    [SetUp]
    public void Setup()
    {
        _mockClient = Substitute.For<IMqttManagedClient>();
        _mockLogger = Substitute.For<ILogger<SparkplugTopologyService>>();

        _handler = null;
        _mockClient
            .When(x => x.ApplicationMessageReceivedAsync += Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>())
            .Do(x => _handler = x.Arg<Func<MqttApplicationMessageReceivedEventArgs, Task>>());

        _service = new SparkplugTopologyService(_mockClient, _mockLogger);
    }

    [TearDown]
    public void TearDown()
    {
        _service.Dispose();
        _mockClient.Dispose();
    }

    private static MqttApplicationMessageReceivedEventArgs MakeArgs(string topic, byte[] payload)
    {
        var appMsg = new MqttApplicationMessageBuilder()
            .WithTopic(topic).WithPayload(payload).Build();
        var packet = new MQTTnet.Packets.MqttPublishPacket { Topic = topic };
        return new MqttApplicationMessageReceivedEventArgs("test-client", appMsg, packet, null);
    }

    private Task Fire(string topic, byte[] payload) => _handler!(MakeArgs(topic, payload));

    private static byte[] SpbPayload(params (string Name, ulong Alias, uint Datatype, double DoubleValue)[] metrics)
    {
        var p = new Payload { Timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
        foreach (var (name, alias, datatype, doubleValue) in metrics)
        {
            var m = new Payload.Types.Metric { Datatype = datatype, DoubleValue = doubleValue };
            if (!string.IsNullOrEmpty(name))
                m.Name = name;
            if (alias != 0)
                m.Alias = alias;
            p.Metrics.Add(m);
        }

        return p.ToByteArray();
    }

    [Test]
    public void TryParseTopic_NonSpbPrefix_ReturnsFalse()
    {
        var result = SparkplugTopologyService.TryParseTopic(
            "sensors/temperature", out _, out _, out _, out _);

        result.Should().BeFalse();
    }

    [Test]
    public void TryParseTopic_StateVerb_ReturnsFalse()
    {
        var result = SparkplugTopologyService.TryParseTopic(
            "spBv1.0/STATE/online", out _, out _, out _, out _);

        result.Should().BeFalse();
    }

    [Test]
    public void TryParseTopic_NodeMessage_ParsesCorrectly()
    {
        var result = SparkplugTopologyService.TryParseTopic(
            "spBv1.0/factory/NBIRTH/edge-01",
            out var group, out var verb, out var node, out var device);

        result.Should().BeTrue();
        group.Should().Be("factory");
        verb.Should().Be("NBIRTH");
        node.Should().Be("edge-01");
        device.Should().BeNull();
    }

    [Test]
    public void TryParseTopic_DeviceMessage_ParsesWithDevice()
    {
        var result = SparkplugTopologyService.TryParseTopic(
            "spBv1.0/factory/DBIRTH/edge-01/sensor-A",
            out _, out var verb, out _, out var device);

        result.Should().BeTrue();
        verb.Should().Be("DBIRTH");
        device.Should().Be("sensor-A");
    }

    [Test]
    public async Task NBIRTH_CreatesOnlineNodeWithMetrics()
    {
        await Fire("spBv1.0/factory/NBIRTH/edge-01",
            SpbPayload(("Temperature", 0, 10, 23.5), ("Pressure", 0, 10, 1013.2)));

        _service.Groups.Should().ContainKey("factory");
        var node = _service.Groups["factory"].Nodes["edge-01"];
        node.Status.Should().Be(SpbNodeStatus.Online);
        node.Metrics.Should().HaveCount(2);
        node.Metrics.Should().Contain(m => m.Name == "Temperature");
        node.Metrics.Should().Contain(m => m.Name == "Pressure");
    }

    [Test]
    public async Task NBIRTH_Reconnect_ClearsOldMetrics()
    {
        await Fire("spBv1.0/g/NBIRTH/n", SpbPayload(("OldMetric", 0, 10, 20.0)));
        await Fire("spBv1.0/g/NBIRTH/n", SpbPayload(("NewMetric", 0, 10, 20.0)));

        var node = _service.Groups["g"].Nodes["n"];
        node.Metrics.Should().HaveCount(1);
        node.Metrics.Single().Name.Should().Be("NewMetric");
    }

    [Test]
    public async Task NBIRTH_PublishesStableMetricSnapshotForReaders()
    {
        await Fire("spBv1.0/g/NBIRTH/n", SpbPayload(("OldMetric", 0, 10, 20.0)));
        var node = _service.Groups["g"].Nodes["n"];
        var snapshot = node.Metrics;

        await Fire("spBv1.0/g/NBIRTH/n", SpbPayload(("NewMetric", 0, 10, 20.0)));

        snapshot.Should().HaveCount(1);
        snapshot.Single().Name.Should().Be("OldMetric");
        node.Metrics.Should().HaveCount(1);
        node.Metrics.Single().Name.Should().Be("NewMetric");
    }

    [Test]
    public async Task NDEATH_MarksNodeOffline()
    {
        await Fire("spBv1.0/g/NBIRTH/n", SpbPayload(("Metric", 0, 10, 1.0)));
        await Fire("spBv1.0/g/NDEATH/n", Array.Empty<byte>());

        _service.Groups["g"].Nodes["n"].Status.Should().Be(SpbNodeStatus.Offline);
    }

    [Test]
    public async Task NDEATH_PropagatesOfflineToAllDevices()
    {
        await Fire("spBv1.0/g/NBIRTH/n", SpbPayload(("Metric", 0, 10, 1.0)));
        await Fire("spBv1.0/g/DBIRTH/n/d1", SpbPayload(("DevMetric", 0, 10, 1.0)));
        await Fire("spBv1.0/g/NDEATH/n", Array.Empty<byte>());

        _service.Groups["g"].Nodes["n"].Devices["d1"].Status.Should().Be(SpbNodeStatus.Offline);
    }

    [Test]
    public async Task NDATA_UpdatesMetricByName()
    {
        await Fire("spBv1.0/g/NBIRTH/n", SpbPayload(("Temp", 0, 10, 20.0)));
        await Fire("spBv1.0/g/NDATA/n", SpbPayload(("Temp", 0, 10, 25.0)));

        var node = _service.Groups["g"].Nodes["n"];
        node.Metrics.Should().HaveCount(1);
        node.Metrics.Single().Value.Should().Be("25.0000");
    }

    [Test]
    public async Task NDATA_PublishesStableMetricSnapshotForReaders()
    {
        await Fire("spBv1.0/g/NBIRTH/n", SpbPayload(("Temp", 0, 10, 20.0)));
        var node = _service.Groups["g"].Nodes["n"];
        var snapshot = node.Metrics;

        await Fire("spBv1.0/g/NDATA/n", SpbPayload(("Pressure", 0, 10, 101.0)));

        snapshot.Should().HaveCount(1);
        snapshot.Single().Name.Should().Be("Temp");
        node.Metrics.Should().HaveCount(2);
        node.Metrics.Should().Contain(m => m.Name == "Pressure");
    }

    [Test]
    public async Task NDATA_UpdatesMetricByAlias()
    {
        await Fire("spBv1.0/g/NBIRTH/n", SpbPayload(("Temp", 42, 10, 20.0)));
        await Fire("spBv1.0/g/NDATA/n", SpbPayload(("", 42, 10, 30.0)));

        _service.Groups["g"].Nodes["n"].Metrics.Single().Value.Should().Be("30.0000");
    }

    [Test]
    public async Task NBIRTH_PreservesAliasOnSnapshot()
    {
        await Fire("spBv1.0/g/NBIRTH/n", SpbPayload(("Temp", 42, 10, 20.0)));

        var node = _service.Groups["g"].Nodes["n"];
        node.Metrics.Single().Alias.Should().Be(42UL);
    }

    [Test]
    public async Task NBIRTH_NullAliasWhenZero()
    {
        await Fire("spBv1.0/g/NBIRTH/n", SpbPayload(("Temp", 0, 10, 20.0)));

        var node = _service.Groups["g"].Nodes["n"];
        node.Metrics.Single().Alias.Should().BeNull();
    }

    [Test]
    public async Task NDATA_PreservesAliasOnSnapshot()
    {
        await Fire("spBv1.0/g/NBIRTH/n", SpbPayload(("Temp", 42, 10, 20.0)));
        await Fire("spBv1.0/g/NDATA/n", SpbPayload(("", 42, 10, 25.0)));

        _service.Groups["g"].Nodes["n"].Metrics.Single().Alias.Should().Be(42UL);
    }

    [Test]
    public async Task DBIRTH_PreservesAliasOnSnapshot()
    {
        await Fire("spBv1.0/g/NBIRTH/n", SpbPayload());
        await Fire("spBv1.0/g/DBIRTH/n/d1", SpbPayload(("Voltage", 7, 10, 220.0)));

        var device = _service.Groups["g"].Nodes["n"].Devices["d1"];
        device.Metrics.Single().Alias.Should().Be(7UL);
    }

    [Test]
    public async Task DDATA_PreservesAliasOnSnapshot()
    {
        await Fire("spBv1.0/g/NBIRTH/n", SpbPayload());
        await Fire("spBv1.0/g/DBIRTH/n/d1", SpbPayload(("Current", 99, 10, 1.5)));
        await Fire("spBv1.0/g/DDATA/n/d1", SpbPayload(("", 99, 10, 2.0)));

        var device = _service.Groups["g"].Nodes["n"].Devices["d1"];
        device.Metrics.Single().Alias.Should().Be(99UL);
    }

    [Test]
    public async Task NDATA_UnknownAlias_DoesNotAddOrUpdateMetric()
    {
        await Fire("spBv1.0/g/NBIRTH/n", SpbPayload(("Temp", 0, 10, 20.0)));
        await Fire("spBv1.0/g/NDATA/n", SpbPayload(("", 999, 10, 99.0)));

        _service.Groups["g"].Nodes["n"].Metrics.Should().HaveCount(1);
        _service.Groups["g"].Nodes["n"].Metrics.Single().Value.Should().Be("20.0000");
    }

    [Test]
    public async Task DBIRTH_RegistersDeviceOnlineWithMetrics()
    {
        await Fire("spBv1.0/g/NBIRTH/n", SpbPayload());
        await Fire("spBv1.0/g/DBIRTH/n/d1", SpbPayload(("Voltage", 0, 10, 220.0)));

        var device = _service.Groups["g"].Nodes["n"].Devices["d1"];
        device.Status.Should().Be(SpbNodeStatus.Online);
        device.Metrics.Should().HaveCount(1);
        device.Metrics.Single().Name.Should().Be("Voltage");
    }

    [Test]
    public async Task DBIRTH_PublishesStableMetricSnapshotForReaders()
    {
        await Fire("spBv1.0/g/DBIRTH/n/d1", SpbPayload(("OldMetric", 0, 10, 1.0)));
        var device = _service.Groups["g"].Nodes["n"].Devices["d1"];
        var snapshot = device.Metrics;

        await Fire("spBv1.0/g/DBIRTH/n/d1", SpbPayload(("NewMetric", 0, 10, 2.0)));

        snapshot.Should().HaveCount(1);
        snapshot.Single().Name.Should().Be("OldMetric");
        device.Metrics.Should().HaveCount(1);
        device.Metrics.Single().Name.Should().Be("NewMetric");
    }

    [Test]
    public async Task DDEATH_MarksDeviceOffline()
    {
        await Fire("spBv1.0/g/NBIRTH/n", SpbPayload());
        await Fire("spBv1.0/g/DBIRTH/n/d1", SpbPayload(("DevMetric", 0, 10, 1.0)));
        await Fire("spBv1.0/g/DDEATH/n/d1", Array.Empty<byte>());

        _service.Groups["g"].Nodes["n"].Devices["d1"].Status.Should().Be(SpbNodeStatus.Offline);
    }

    [Test]
    public async Task DDATA_UpdatesDeviceMetric()
    {
        await Fire("spBv1.0/g/NBIRTH/n", SpbPayload());
        await Fire("spBv1.0/g/DBIRTH/n/d1", SpbPayload(("Current", 0, 10, 1.5)));
        await Fire("spBv1.0/g/DDATA/n/d1", SpbPayload(("Current", 0, 10, 2.0)));

        var device = _service.Groups["g"].Nodes["n"].Devices["d1"];
        device.Metrics.Should().HaveCount(1);
        device.Metrics.Single().Value.Should().Be("2.0000");
    }

    [Test]
    public async Task DDATA_PublishesStableMetricSnapshotForReaders()
    {
        await Fire("spBv1.0/g/DBIRTH/n/d1", SpbPayload(("Current", 0, 10, 1.5)));
        var device = _service.Groups["g"].Nodes["n"].Devices["d1"];
        var snapshot = device.Metrics;

        await Fire("spBv1.0/g/DDATA/n/d1", SpbPayload(("Voltage", 0, 10, 220.0)));

        snapshot.Should().HaveCount(1);
        snapshot.Single().Name.Should().Be("Current");
        device.Metrics.Should().HaveCount(2);
        device.Metrics.Should().Contain(m => m.Name == "Voltage");
    }

    [Test]
    public async Task NBIRTH_RaisesTopologyChanged()
    {
        var raised = false;
        _service.TopologyChanged += () => raised = true;

        await Fire("spBv1.0/g/NBIRTH/n", SpbPayload(("Temp", 0, 10, 20.0)));

        raised.Should().BeTrue();
    }

    [Test]
    public async Task NDEATH_RaisesTopologyChanged()
    {
        var raised = false;
        _service.TopologyChanged += () => raised = true;

        await Fire("spBv1.0/g/NDEATH/n", Array.Empty<byte>());

        raised.Should().BeTrue();
    }

    [Test]
    public async Task NDATA_RaisesTopologyChanged()
    {
        await Fire("spBv1.0/g/NBIRTH/n", SpbPayload(("Temp", 0, 10, 20.0)));
        var raised = false;
        _service.TopologyChanged += () => raised = true;

        await Fire("spBv1.0/g/NDATA/n", SpbPayload(("Temp", 0, 10, 25.0)));

        raised.Should().BeTrue();
    }

    [Test]
    public async Task DBIRTH_RaisesTopologyChanged()
    {
        var raised = false;
        _service.TopologyChanged += () => raised = true;

        await Fire("spBv1.0/g/DBIRTH/n/d1", SpbPayload(("Voltage", 0, 10, 220.0)));

        raised.Should().BeTrue();
    }

    [Test]
    public async Task DDEATH_RaisesTopologyChanged()
    {
        var raised = false;
        _service.TopologyChanged += () => raised = true;

        await Fire("spBv1.0/g/DDEATH/n/d1", Array.Empty<byte>());

        raised.Should().BeTrue();
    }

    [Test]
    public async Task DDATA_RaisesTopologyChanged()
    {
        await Fire("spBv1.0/g/DBIRTH/n/d1", SpbPayload(("Current", 0, 10, 1.5)));
        var raised = false;
        _service.TopologyChanged += () => raised = true;

        await Fire("spBv1.0/g/DDATA/n/d1", SpbPayload(("Current", 0, 10, 2.0)));

        raised.Should().BeTrue();
    }

    [Test]
    public void Dispose_UnregistersMessageHandler()
    {
        var unsubscribed = false;
        _mockClient
            .When(x => x.ApplicationMessageReceivedAsync -= Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>())
            .Do(_ => unsubscribed = true);

        _service.Dispose();

        unsubscribed.Should().BeTrue("Dispose must unregister the ApplicationMessageReceivedAsync handler");
    }

    [Test]
    public async Task RemoveNode_ExistingNode_RemovesAndRaisesEvent()
    {
        await Fire("spBv1.0/factory/NBIRTH/edge-01", SpbPayload());
        await Fire("spBv1.0/factory/NBIRTH/edge-02", SpbPayload());
        var raised = 0;
        _service.TopologyChanged += () => raised++;

        var result = _service.RemoveNode("factory", "edge-01");

        result.Should().BeTrue();
        // group still exists because edge-02 remains; without the second seed, _service.Groups["factory"] would throw after the removal
        _service.Groups["factory"].Nodes.Should().NotContainKey("edge-01");
        _service.Groups["factory"].Nodes.Should().ContainKey("edge-02");
        raised.Should().Be(1);
    }

    [Test]
    public async Task RemoveNode_NonExistentNode_ReturnsFalseAndDoesNotRaise()
    {
        await Fire("spBv1.0/factory/NBIRTH/edge-01", SpbPayload());
        var raised = 0;
        _service.TopologyChanged += () => raised++;

        var result = _service.RemoveNode("factory", "edge-99");

        result.Should().BeFalse();
        _service.Groups["factory"].Nodes.Should().ContainKey("edge-01");
        raised.Should().Be(0);
    }

    [Test]
    public async Task RemoveNode_NonExistentGroup_ReturnsFalseAndDoesNotRaise()
    {
        var raised = 0;
        _service.TopologyChanged += () => raised++;

        var result = _service.RemoveNode("missing", "edge-01");

        result.Should().BeFalse();
        raised.Should().Be(0);
    }

    [Test]
    public async Task RemoveNode_LastNodeInGroup_AlsoPrunesGroup()
    {
        await Fire("spBv1.0/factory/NBIRTH/edge-01", SpbPayload());
        var raised = 0;
        _service.TopologyChanged += () => raised++;
        _service.Groups.Should().ContainKey("factory");

        var result = _service.RemoveNode("factory", "edge-01");

        result.Should().BeTrue();
        _service.Groups.Should().NotContainKey("factory");
        raised.Should().Be(1);
    }

    [Test]
    public async Task RemoveOfflineNodes_EmptyTopology_ReturnsZero()
    {
        var result = _service.RemoveOfflineNodes();

        result.Should().Be(0);
    }

    [Test]
    public async Task RemoveOfflineNodes_OnlyRemovesOfflineNodes_LeavesOnlineAndUnknownUntouched()
    {
        await Fire("spBv1.0/factory/NBIRTH/online-1", SpbPayload());
        await Fire("spBv1.0/factory/NBIRTH/offline-1", SpbPayload());
        await Fire("spBv1.0/factory/NDEATH/offline-1", Array.Empty<byte>());
        var unknown = new SpbNode { NodeId = "unknown-1", GroupId = "factory", Status = SpbNodeStatus.Unknown };
        _service.Groups["factory"].Nodes["unknown-1"] = unknown;

        var result = _service.RemoveOfflineNodes();

        result.Should().Be(1);
        _service.Groups["factory"].Nodes.Should().ContainKeys("online-1", "unknown-1");
        _service.Groups["factory"].Nodes.Should().NotContainKey("offline-1");
    }

    [Test]
    public async Task RemoveOfflineNodes_MixedTopology_RemovesAllAndPrunesEmptyGroups()
    {
        await Fire("spBv1.0/factory/NBIRTH/online-1", SpbPayload());
        await Fire("spBv1.0/factory/NBIRTH/offline-1", SpbPayload());
        await Fire("spBv1.0/factory/NDEATH/offline-1", Array.Empty<byte>());
        await Fire("spBv1.0/plant/NBIRTH/only-offline", SpbPayload());
        await Fire("spBv1.0/plant/NDEATH/only-offline", Array.Empty<byte>());

        var result = _service.RemoveOfflineNodes();

        result.Should().Be(2);
        _service.Groups.Should().ContainKey("factory");
        _service.Groups["factory"].Nodes.Should().ContainSingle();
        _service.Groups.Should().NotContainKey("plant");
    }

    [Test]
    public async Task RemoveOfflineNodes_RaisesEventExactlyOnce()
    {
        await Fire("spBv1.0/factory/NBIRTH/edge-01", SpbPayload());
        await Fire("spBv1.0/factory/NDEATH/edge-01", Array.Empty<byte>());
        await Fire("spBv1.0/factory/NBIRTH/edge-02", SpbPayload());
        await Fire("spBv1.0/factory/NDEATH/edge-02", Array.Empty<byte>());

        var raised = 0;
        _service.TopologyChanged += () => raised++;

        _service.RemoveOfflineNodes();

        raised.Should().Be(1);
    }

    [Test]
    public async Task RemoveOfflineNodes_NoOfflineNodes_DoesNotRaiseEvent()
    {
        await Fire("spBv1.0/factory/NBIRTH/online-1", SpbPayload());
        var raised = 0;
        _service.TopologyChanged += () => raised++;

        var result = _service.RemoveOfflineNodes();

        result.Should().Be(0);
        raised.Should().Be(0);
    }

    [Test]
    public async Task RemoveNode_AfterRemoval_NextNBirthRecreatesTheNode()
    {
        await Fire("spBv1.0/factory/NBIRTH/edge-01", SpbPayload(("X", 0, 10, 1.0)));
        _service.RemoveNode("factory", "edge-01");
        _service.Groups.Should().NotContainKey("factory");

        await Fire("spBv1.0/factory/NBIRTH/edge-01", SpbPayload(("Y", 0, 10, 2.0)));

        var node = _service.Groups["factory"].Nodes["edge-01"];
        node.Status.Should().Be(SpbNodeStatus.Online);
        node.Metrics.Should().ContainSingle().Which.Name.Should().Be("Y");
    }

    [Test]
    public async Task NDATA_WithoutPriorBirth_RequestsRebirth()
    {
        await Fire("spBv1.0/factory/NDATA/edge-01", SpbPayload(("Temp", 0, 10, 22.0)));

        await _mockClient.Received(1).EnqueueAsync(
            Arg.Is<MqttApplicationMessage>(m =>
                m!.Topic == "spBv1.0/factory/NCMD/edge-01"));
    }

    [Test]
    public async Task NDATA_AfterBirth_DoesNotRequestRebirth()
    {
        await Fire("spBv1.0/factory/NBIRTH/edge-01", SpbPayload(("Temp", 0, 10, 20.0)));
        await Fire("spBv1.0/factory/NDATA/edge-01", SpbPayload(("Temp", 0, 10, 25.0)));

        await _mockClient.DidNotReceive().EnqueueAsync(
            Arg.Any<MqttApplicationMessage>());
    }

    [Test]
    public async Task NDATA_WithoutBirth_RateLimitsRebirthRequests()
    {
        await Fire("spBv1.0/factory/NDATA/edge-01", SpbPayload(("Temp", 0, 10, 20.0)));
        await Fire("spBv1.0/factory/NDATA/edge-01", SpbPayload(("Temp", 0, 10, 21.0)));
        await Fire("spBv1.0/factory/NDATA/edge-01", SpbPayload(("Temp", 0, 10, 22.0)));

        await _mockClient.Received(1).EnqueueAsync(
            Arg.Is<MqttApplicationMessage>(m =>
                m!.Topic == "spBv1.0/factory/NCMD/edge-01"));
    }

    [Test]
    public async Task NDATA_WithoutBirth_AfterCooldownExpires_RequestsRebirthAgain()
    {
        var fakeClock = new FakeTimeProvider();
        _service.Dispose();
        _service = new SparkplugTopologyService(_mockClient, _mockLogger, fakeClock);
        // _service field updated so TearDown disposes the correct instance

        // First NDATA without birth — triggers rebirth
        await Fire("spBv1.0/factory/NDATA/edge-01", SpbPayload(("Temp", 0, 10, 22.0)));
        await _mockClient.Received(1).EnqueueAsync(
            Arg.Is<MqttApplicationMessage>(m =>
                m!.Topic == "spBv1.0/factory/NCMD/edge-01"));

        // Advance past the 30-second cooldown
        fakeClock.Advance(TimeSpan.FromSeconds(31));

        // Second NDATA without birth — should trigger another rebirth
        await Fire("spBv1.0/factory/NDATA/edge-01", SpbPayload(("Temp", 0, 10, 23.0)));
        await _mockClient.Received(2).EnqueueAsync(
            Arg.Is<MqttApplicationMessage>(m =>
                m!.Topic == "spBv1.0/factory/NCMD/edge-01"));
    }

    [Test]
    public async Task NDATA_WithoutBirth_ConcurrentRequests_OnlyOneRebirthPublished()
    {
        var tasks = Enumerable.Range(0, 10).Select(_ =>
            Fire("spBv1.0/factory/NDATA/edge-01", SpbPayload(("Temp", 0, 10, 22.0))));

        await Task.WhenAll(tasks);

        await _mockClient.Received(1).EnqueueAsync(
            Arg.Is<MqttApplicationMessage>(m =>
                m!.Topic == "spBv1.0/factory/NCMD/edge-01"));
    }

    [Test]
    public async Task NDATA_WithoutBirth_EnqueueAsyncThrows_DoesNotPropagateException()
    {
        _mockClient.EnqueueAsync(Arg.Any<MqttApplicationMessage>())
            .Returns(Task.FromException(new InvalidOperationException("connection lost")));

        var act = () => Fire("spBv1.0/factory/NDATA/edge-01", SpbPayload(("Temp", 0, 10, 22.0)));

        await act.Should().NotThrowAsync();
        // LogWarning is an extension method; verify via the Log interface method
        await _mockClient.Received(1).EnqueueAsync(Arg.Any<MqttApplicationMessage>());
    }

    [Test]
    public async Task NDATA_WithoutBirth_SetsLastRebirthRequestAt()
    {
        await Fire("spBv1.0/factory/NDATA/edge-01", SpbPayload(("Temp", 0, 10, 22.0)));

        var node = _service.Groups["factory"].Nodes["edge-01"];
        node.LastRebirthRequestAt.Should().NotBeNull();
        node.LastRebirthRequestAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task DDATA_WithoutNodeBirth_RequestsRebirth()
    {
        await Fire("spBv1.0/factory/DDATA/edge-01/sensor-A", SpbPayload(("Voltage", 0, 10, 220.0)));

        await _mockClient.Received(1).EnqueueAsync(
            Arg.Is<MqttApplicationMessage>(m =>
                m!.Topic == "spBv1.0/factory/NCMD/edge-01"));
    }

    [Test]
    public async Task RequestNodeRebirthAsync_PublishesRebirthCommand()
    {
        await Fire("spBv1.0/factory/NBIRTH/edge-01", SpbPayload(("Temp", 0, 10, 20.0)));

        await _service.RequestNodeRebirthAsync("factory", "edge-01");

        await _mockClient.Received(1).EnqueueAsync(
            Arg.Is<MqttApplicationMessage>(m =>
                m!.Topic == "spBv1.0/factory/NCMD/edge-01"));
    }

    [Test]
    public async Task RequestNodeRebirthAsync_NonExistentNode_DoesNotPublish()
    {
        await _service.RequestNodeRebirthAsync("missing", "edge-01");

        await _mockClient.DidNotReceive().EnqueueAsync(
            Arg.Any<MqttApplicationMessage>());
    }

    [Test]
    public async Task RebirthPayload_ContainsNodeControlRebirthMetric()
    {
        await Fire("spBv1.0/factory/NDATA/edge-01", SpbPayload(("Temp", 0, 10, 22.0)));

        await _mockClient.Received(1).EnqueueAsync(
            Arg.Is<MqttApplicationMessage>(m =>
                m!.Topic == "spBv1.0/factory/NCMD/edge-01"
                && VerifyRebirthPayload(m!.GetPayloadSegment())));
    }

    private static bool VerifyRebirthPayload(ReadOnlyMemory<byte> payloadBytes)
    {
        var payload = Payload.Parser.ParseFrom(payloadBytes.ToArray());
        return payload.Metrics.Count == 1
            && payload.Metrics[0].Name == "Node Control/Rebirth"
            && payload.Metrics[0].Datatype == 11
            && payload.Metrics[0].BooleanValue;
    }

    [Test]
    public async Task NDATA_WithoutBirth_PublishesRebirthCommandWithQoS1()
    {
        await Fire("spBv1.0/factory/NDATA/edge-01", SpbPayload(("Temp", 0, 10, 22.0)));

        await _mockClient.Received(1).EnqueueAsync(
            Arg.Is<MqttApplicationMessage>(m =>
                m!.Topic == "spBv1.0/factory/NCMD/edge-01"
                && m!.QualityOfServiceLevel == MqttQualityOfServiceLevel.AtLeastOnce));
    }

    [Test]
    public async Task ClearAll_WithPopulatedTopology_ClearsAllGroups()
    {
        var nbirth = SpbPayload(("metric1", 0, 10, 1.0));
        await Fire("spBv1.0/grp/NBIRTH/node1", nbirth);
        var dbirth = SpbPayload(("dmetric", 0, 12, 42.0));
        await Fire("spBv1.0/grp/DBIRTH/node1/device1", dbirth);

        _service.Groups.Should().NotBeEmpty();

        _service.ClearAll();

        _service.Groups.Should().BeEmpty();
    }

    [Test]
    public void ClearAll_WhenEmpty_DoesNotThrow()
    {
        var act = () => _service.ClearAll();

        act.Should().NotThrow();
    }

    [Test]
    public async Task ClearAll_RaisesTopologyChanged()
    {
        var nbirth = SpbPayload(("m", 0, 10, 1.0));
        await Fire("spBv1.0/grp/NBIRTH/node1", nbirth);

        var raised = false;
        _service.TopologyChanged += () => raised = true;

        _service.ClearAll();

        raised.Should().BeTrue();
    }

    [Test]
    public void ClearAll_WhenEmpty_DoesNotRaiseTopologyChanged()
    {
        var raised = false;
        _service.TopologyChanged += () => raised = true;

        _service.ClearAll();

        raised.Should().BeFalse();
    }

    [Test]
    public async Task ClearAll_ThenReceiveMessage_BuildsNewTopology()
    {
        var nbirth = SpbPayload(("m", 0, 10, 1.0));
        await Fire("spBv1.0/grp/NBIRTH/node1", nbirth);
        _service.ClearAll();
        _service.Groups.Should().BeEmpty();

        await Fire("spBv1.0/grp/NBIRTH/node2", nbirth);

        _service.Groups.Should().ContainKey("grp");
        _service.Groups["grp"].Nodes.Should().ContainKey("node2");
    }

    // --- ApplyTopologyEvents tests ---

    private static MetricSnapshot Ms(string name, string dataType = "double", string value = "1.0000") =>
        new() { Name = name, DataType = dataType, Value = value };

    [Test]
    public void ApplyTopologyEvents_NodeBirth_CreatesOnlineNodeWithMetrics()
    {
        _service.ApplyTopologyEvents(
        [
            new NodeBirthEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/factory/NBIRTH/edge-01",
                GroupId = "factory",
                NodeId = "edge-01",
                Metrics = [Ms("Temperature", "double", "23.5000"), Ms("Pressure", "double", "1013.2000")]
            }
        ]);

        _service.Groups.Should().ContainKey("factory");
        var node = _service.Groups["factory"].Nodes["edge-01"];
        node.Status.Should().Be(SpbNodeStatus.Online);
        node.Metrics.Should().HaveCount(2);
        node.Metrics.Should().Contain(m => m.Name == "Temperature" && m.Value == "23.5000");
        node.Metrics.Should().Contain(m => m.Name == "Pressure" && m.Value == "1013.2000");
    }

    [Test]
    public void ApplyTopologyEvents_NodeBirth_ClearsOldMetrics()
    {
        _service.ApplyTopologyEvents(
        [
            new NodeBirthEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/NBIRTH/n",
                GroupId = "g",
                NodeId = "n",
                Metrics = [Ms("OldMetric")]
            }
        ]);

        _service.ApplyTopologyEvents(
        [
            new NodeBirthEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/NBIRTH/n",
                GroupId = "g",
                NodeId = "n",
                Metrics = [Ms("NewMetric")]
            }
        ]);

        var node = _service.Groups["g"].Nodes["n"];
        node.Metrics.Should().HaveCount(1);
        node.Metrics.Single().Name.Should().Be("NewMetric");
    }

    [Test]
    public void ApplyTopologyEvents_NodeDeath_MarksNodeOffline()
    {
        _service.ApplyTopologyEvents(
        [
            new NodeBirthEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/NBIRTH/n",
                GroupId = "g",
                NodeId = "n",
                Metrics = [Ms("Metric")]
            }
        ]);

        _service.ApplyTopologyEvents(
        [
            new NodeDeathEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/NDEATH/n",
                GroupId = "g",
                NodeId = "n"
            }
        ]);

        _service.Groups["g"].Nodes["n"].Status.Should().Be(SpbNodeStatus.Offline);
    }

    [Test]
    public void ApplyTopologyEvents_NodeDeath_PropagatesOfflineToAllDevices()
    {
        _service.ApplyTopologyEvents(
        [
            new NodeBirthEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/NBIRTH/n",
                GroupId = "g",
                NodeId = "n",
                Metrics = []
            },
            new DeviceBirthEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/DBIRTH/n/d1",
                GroupId = "g",
                NodeId = "n",
                DeviceId = "d1",
                Metrics = [Ms("DevMetric")]
            }
        ]);

        _service.ApplyTopologyEvents(
        [
            new NodeDeathEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/NDEATH/n",
                GroupId = "g",
                NodeId = "n"
            }
        ]);

        _service.Groups["g"].Nodes["n"].Devices["d1"].Status.Should().Be(SpbNodeStatus.Offline);
    }

    [Test]
    public void ApplyTopologyEvents_NodeData_UpdatesExistingMetric()
    {
        _service.ApplyTopologyEvents(
        [
            new NodeBirthEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/NBIRTH/n",
                GroupId = "g",
                NodeId = "n",
                Metrics = [Ms("Temp", "double", "20.0000")]
            }
        ]);

        _service.ApplyTopologyEvents(
        [
            new NodeDataEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/NDATA/n",
                GroupId = "g",
                NodeId = "n",
                Metrics = [Ms("Temp", "double", "25.0000")]
            }
        ]);

        var node = _service.Groups["g"].Nodes["n"];
        node.Metrics.Should().HaveCount(1);
        node.Metrics.Single().Value.Should().Be("25.0000");
    }

    [Test]
    public void ApplyTopologyEvents_NodeData_AddsNewMetric()
    {
        _service.ApplyTopologyEvents(
        [
            new NodeBirthEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/NBIRTH/n",
                GroupId = "g",
                NodeId = "n",
                Metrics = [Ms("Temp", "double", "20.0000")]
            }
        ]);

        _service.ApplyTopologyEvents(
        [
            new NodeDataEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/NDATA/n",
                GroupId = "g",
                NodeId = "n",
                Metrics = [Ms("Pressure", "double", "101.0000")]
            }
        ]);

        var node = _service.Groups["g"].Nodes["n"];
        node.Metrics.Should().HaveCount(2);
        node.Metrics.Should().Contain(m => m.Name == "Pressure");
    }

    [Test]
    public void ApplyTopologyEvents_DeviceBirth_CreatesOnlineDeviceWithMetrics()
    {
        _service.ApplyTopologyEvents(
        [
            new NodeBirthEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/NBIRTH/n",
                GroupId = "g",
                NodeId = "n",
                Metrics = []
            }
        ]);

        _service.ApplyTopologyEvents(
        [
            new DeviceBirthEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/DBIRTH/n/d1",
                GroupId = "g",
                NodeId = "n",
                DeviceId = "d1",
                Metrics = [Ms("Voltage", "double", "220.0000")]
            }
        ]);

        var device = _service.Groups["g"].Nodes["n"].Devices["d1"];
        device.Status.Should().Be(SpbNodeStatus.Online);
        device.Metrics.Should().HaveCount(1);
        device.Metrics.Single().Name.Should().Be("Voltage");
    }

    [Test]
    public void ApplyTopologyEvents_DeviceDeath_MarksDeviceOffline()
    {
        _service.ApplyTopologyEvents(
        [
            new NodeBirthEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/NBIRTH/n",
                GroupId = "g",
                NodeId = "n",
                Metrics = []
            },
            new DeviceBirthEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/DBIRTH/n/d1",
                GroupId = "g",
                NodeId = "n",
                DeviceId = "d1",
                Metrics = [Ms("DevMetric")]
            }
        ]);

        _service.ApplyTopologyEvents(
        [
            new DeviceDeathEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/DDEATH/n/d1",
                GroupId = "g",
                NodeId = "n",
                DeviceId = "d1"
            }
        ]);

        _service.Groups["g"].Nodes["n"].Devices["d1"].Status.Should().Be(SpbNodeStatus.Offline);
    }

    [Test]
    public void ApplyTopologyEvents_DeviceData_UpdatesDeviceMetric()
    {
        _service.ApplyTopologyEvents(
        [
            new NodeBirthEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/NBIRTH/n",
                GroupId = "g",
                NodeId = "n",
                Metrics = []
            },
            new DeviceBirthEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/DBIRTH/n/d1",
                GroupId = "g",
                NodeId = "n",
                DeviceId = "d1",
                Metrics = [Ms("Current", "double", "1.5000")]
            }
        ]);

        _service.ApplyTopologyEvents(
        [
            new DeviceDataEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/DDATA/n/d1",
                GroupId = "g",
                NodeId = "n",
                DeviceId = "d1",
                Metrics = [Ms("Current", "double", "2.0000")]
            }
        ]);

        var device = _service.Groups["g"].Nodes["n"].Devices["d1"];
        device.Metrics.Should().HaveCount(1);
        device.Metrics.Single().Value.Should().Be("2.0000");
    }

    [Test]
    public void ApplyTopologyEvents_DeviceData_AddsNewDeviceMetric()
    {
        _service.ApplyTopologyEvents(
        [
            new NodeBirthEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/NBIRTH/n",
                GroupId = "g",
                NodeId = "n",
                Metrics = []
            },
            new DeviceBirthEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/DBIRTH/n/d1",
                GroupId = "g",
                NodeId = "n",
                DeviceId = "d1",
                Metrics = [Ms("Current", "double", "1.5000")]
            }
        ]);

        _service.ApplyTopologyEvents(
        [
            new DeviceDataEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/DDATA/n/d1",
                GroupId = "g",
                NodeId = "n",
                DeviceId = "d1",
                Metrics = [Ms("Voltage", "double", "220.0000")]
            }
        ]);

        var device = _service.Groups["g"].Nodes["n"].Devices["d1"];
        device.Metrics.Should().HaveCount(2);
        device.Metrics.Should().Contain(m => m.Name == "Voltage");
    }

    [Test]
    public void ApplyTopologyEvents_MultipleEventsInSingleCall_ProcessesAll()
    {
        _service.ApplyTopologyEvents(
        [
            new NodeBirthEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/NBIRTH/n",
                GroupId = "g",
                NodeId = "n",
                Metrics = [Ms("Temp", "double", "20.0000")]
            },
            new DeviceBirthEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/DBIRTH/n/d1",
                GroupId = "g",
                NodeId = "n",
                DeviceId = "d1",
                Metrics = [Ms("Voltage", "double", "220.0000")]
            }
        ]);

        _service.Groups["g"].Nodes["n"].Status.Should().Be(SpbNodeStatus.Online);
        _service.Groups["g"].Nodes["n"].Devices["d1"].Status.Should().Be(SpbNodeStatus.Online);
    }

    [Test]
    public void ApplyTopologyEvents_EmptyEvents_DoesNotThrow()
    {
        var act = () => _service.ApplyTopologyEvents([]);

        act.Should().NotThrow();
    }

    [Test]
    public void ApplyTopologyEvents_NodeBirth_RaisesTopologyChanged()
    {
        var raised = false;
        _service.TopologyChanged += () => raised = true;

        _service.ApplyTopologyEvents(
        [
            new NodeBirthEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/NBIRTH/n",
                GroupId = "g",
                NodeId = "n",
                Metrics = [Ms("Temp")]
            }
        ]);

        raised.Should().BeTrue();
    }

    [Test]
    public void ApplyTopologyEvents_NodeDeath_RaisesTopologyChanged()
    {
        _service.ApplyTopologyEvents(
        [
            new NodeBirthEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/NBIRTH/n",
                GroupId = "g",
                NodeId = "n",
                Metrics = []
            }
        ]);

        var raised = false;
        _service.TopologyChanged += () => raised = true;

        _service.ApplyTopologyEvents(
        [
            new NodeDeathEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/NDEATH/n",
                GroupId = "g",
                NodeId = "n"
            }
        ]);

        raised.Should().BeTrue();
    }

    [Test]
    public void ApplyTopologyEvents_NodeData_RaisesTopologyChanged()
    {
        _service.ApplyTopologyEvents(
        [
            new NodeBirthEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/NBIRTH/n",
                GroupId = "g",
                NodeId = "n",
                Metrics = [Ms("Temp")]
            }
        ]);

        var raised = false;
        _service.TopologyChanged += () => raised = true;

        _service.ApplyTopologyEvents(
        [
            new NodeDataEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/NDATA/n",
                GroupId = "g",
                NodeId = "n",
                Metrics = [Ms("Temp", "double", "25.0000")]
            }
        ]);

        raised.Should().BeTrue();
    }

    [Test]
    public void ApplyTopologyEvents_DeviceBirth_RaisesTopologyChanged()
    {
        var raised = false;
        _service.TopologyChanged += () => raised = true;

        _service.ApplyTopologyEvents(
        [
            new DeviceBirthEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/DBIRTH/n/d1",
                GroupId = "g",
                NodeId = "n",
                DeviceId = "d1",
                Metrics = [Ms("Voltage")]
            }
        ]);

        raised.Should().BeTrue();
    }

    [Test]
    public void ApplyTopologyEvents_DeviceDeath_RaisesTopologyChanged()
    {
        _service.ApplyTopologyEvents(
        [
            new DeviceBirthEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/DBIRTH/n/d1",
                GroupId = "g",
                NodeId = "n",
                DeviceId = "d1",
                Metrics = []
            }
        ]);

        var raised = false;
        _service.TopologyChanged += () => raised = true;

        _service.ApplyTopologyEvents(
        [
            new DeviceDeathEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/DDEATH/n/d1",
                GroupId = "g",
                NodeId = "n",
                DeviceId = "d1"
            }
        ]);

        raised.Should().BeTrue();
    }

    [Test]
    public void ApplyTopologyEvents_DeviceData_RaisesTopologyChanged()
    {
        _service.ApplyTopologyEvents(
        [
            new DeviceBirthEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/DBIRTH/n/d1",
                GroupId = "g",
                NodeId = "n",
                DeviceId = "d1",
                Metrics = [Ms("Current")]
            }
        ]);

        var raised = false;
        _service.TopologyChanged += () => raised = true;

        _service.ApplyTopologyEvents(
        [
            new DeviceDataEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/DDATA/n/d1",
                GroupId = "g",
                NodeId = "n",
                DeviceId = "d1",
                Metrics = [Ms("Current", "double", "2.0000")]
            }
        ]);

        raised.Should().BeTrue();
    }

    [Test]
    public void ApplyTopologyEvents_FullLifecycle_MatchesProtobufPath()
    {
        // Simulate: NBIRTH → DBIRTH → NDATA → DDATA → DDEATH → NDEATH
        // via ApplyTopologyEvents, then verify state matches expected

        _service.ApplyTopologyEvents(
        [
            new NodeBirthEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/factory/NBIRTH/edge-01",
                GroupId = "factory",
                NodeId = "edge-01",
                Metrics = [Ms("Temp", "double", "20.0000"), Ms("Pressure", "double", "1013.0000")]
            }
        ]);

        var node = _service.Groups["factory"].Nodes["edge-01"];
        node.Status.Should().Be(SpbNodeStatus.Online);
        node.Metrics.Should().HaveCount(2);

        _service.ApplyTopologyEvents(
        [
            new DeviceBirthEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/factory/DBIRTH/edge-01/sensor-A",
                GroupId = "factory",
                NodeId = "edge-01",
                DeviceId = "sensor-A",
                Metrics = [Ms("Voltage", "double", "220.0000")]
            }
        ]);

        var device = node.Devices["sensor-A"];
        device.Status.Should().Be(SpbNodeStatus.Online);
        device.Metrics.Should().HaveCount(1);

        _service.ApplyTopologyEvents(
        [
            new NodeDataEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/factory/NDATA/edge-01",
                GroupId = "factory",
                NodeId = "edge-01",
                Metrics = [Ms("Temp", "double", "25.0000")]
            },
            new DeviceDataEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/factory/DDATA/edge-01/sensor-A",
                GroupId = "factory",
                NodeId = "edge-01",
                DeviceId = "sensor-A",
                Metrics = [Ms("Voltage", "double", "230.0000")]
            }
        ]);

        node.Metrics.Should().Contain(m => m.Name == "Temp" && m.Value == "25.0000");
        node.Metrics.Should().Contain(m => m.Name == "Pressure" && m.Value == "1013.0000");
        device.Metrics.Single().Value.Should().Be("230.0000");

        _service.ApplyTopologyEvents(
        [
            new DeviceDeathEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/factory/DDEATH/edge-01/sensor-A",
                GroupId = "factory",
                NodeId = "edge-01",
                DeviceId = "sensor-A"
            }
        ]);

        device.Status.Should().Be(SpbNodeStatus.Offline);

        _service.ApplyTopologyEvents(
        [
            new NodeDeathEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/factory/NDEATH/edge-01",
                GroupId = "factory",
                NodeId = "edge-01"
            }
        ]);

        node.Status.Should().Be(SpbNodeStatus.Offline);
        // NDEATH also propagates offline to devices
        device.Status.Should().Be(SpbNodeStatus.Offline);
    }

    [Test]
    public void ApplyTopologyEvents_DeviceBirth_WithoutPriorNodeBirth_CreatesNodeImplicitly()
    {
        _service.ApplyTopologyEvents(
        [
            new DeviceBirthEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/DBIRTH/n/d1",
                GroupId = "g",
                NodeId = "n",
                DeviceId = "d1",
                Metrics = [Ms("Voltage")]
            }
        ]);

        _service.Groups.Should().ContainKey("g");
        _service.Groups["g"].Nodes.Should().ContainKey("n");
        _service.Groups["g"].Nodes["n"].Devices.Should().ContainKey("d1");
        _service.Groups["g"].Nodes["n"].Devices["d1"].Status.Should().Be(SpbNodeStatus.Online);
    }
}
