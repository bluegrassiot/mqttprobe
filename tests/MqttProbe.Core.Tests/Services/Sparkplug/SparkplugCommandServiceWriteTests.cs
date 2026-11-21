using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MQTTnet;
using MQTTnet.Protocol;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Core.Services.Sparkplug;
using MqttProbe.PluginContracts;
using Org.Eclipse.Tahu.Protobuf;

namespace MqttProbe.Core.Tests.Services.Sparkplug;

[TestFixture]
public class SparkplugCommandServiceWriteTests
{
    private IMqttManagedClient _client = null!;
    private SparkplugSettings _settings = null!;
    private SparkplugTopologyService _topology = null!;
    private SparkplugCommandService _service = null!;
    private FakeTimeProvider _clock = null!;
    private List<MqttApplicationMessage> _published = null!;

    [SetUp]
    public async Task Setup()
    {
        _published = [];
        _client = Substitute.For<IMqttManagedClient>();
        _client.TryPublishConnectedAsync(
                Arg.Do<MqttApplicationMessage>(_published.Add),
                Arg.Any<long?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        _settings = new SparkplugSettings { AllowMetricWrites = true, AllowNodeReboot = false };
        var settingsStore = Substitute.For<ISparkplugSettings>();
        settingsStore.Sparkplug.Returns(_settings);

        _topology = new SparkplugTopologyService();
        _clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_123));
        _service = new SparkplugCommandService(
            _client, Substitute.For<ILogger<SparkplugCommandService>>(), settingsStore, _topology, _clock);

        await _topology.ApplyTopologyEventsAsync(
        [
            new NodeBirthEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/NBIRTH/n",
                GroupId = "g",
                NodeId = "n",
                Metrics =
                [
                    Ms("Setpoint", "int32"),
                    Ms("Label", "string"),
                    Ms("Blob", "bytes"),
                    Ms("Node Control/Reboot", "bool"),
                    Ms("Node Control/Rebirth", "bool"),
                    Ms("Node Control/Next Server", "bool"),
                    Ms("Node Control/Scan Rate", "int64")
                ]
            },
            new DeviceBirthEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/DBIRTH/n/d1",
                GroupId = "g",
                NodeId = "n",
                DeviceId = "d1",
                Metrics =
                [
                    Ms("Speed", "double"),
                    Ms("Device Control/Reboot", "bool"),
                    Ms("Device Control/Rebirth", "bool"),
                    Ms("Device Control/Scan Rate", "int64")
                ]
            },
            new NodeDataEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/NDATA/n",
                GroupId = "g",
                NodeId = "n",
                Metrics = [Ms("DataOnly", "int32")]
            }
        ]);
    }

    [TearDown]
    public void TearDown() => _client.Dispose();

    [Test]
    public async Task WriteNodeMetric_PublishesTypedNcmdWithCommandEnvelope()
    {
        var result = await _service.WriteNodeMetricAsync("g", "n", "Setpoint", "-42");

        result.Should().Be(SparkplugCommandResult.Published);
        var message = _published.Should().ContainSingle().Subject;
        message.Topic.Should().Be("spBv1.0/g/NCMD/n");
        message.QualityOfServiceLevel.Should().Be(MqttQualityOfServiceLevel.AtMostOnce);
        message.Retain.Should().BeFalse();

        var payload = Payload.Parser.ParseFrom(message.GetPayloadSegment().ToArray());
        payload.Timestamp.Should().Be(1_800_000_000_123UL);
        payload.Seq.Should().Be(0UL, "proto3 omits a zero seq from the wire");
        var metric = payload.Metrics.Should().ContainSingle().Subject;
        metric.Name.Should().Be("Setpoint");
        metric.Alias.Should().Be(0UL);
        metric.Datatype.Should().Be(3u);
        metric.IntValue.Should().Be(unchecked((uint)-42));
    }

    [Test]
    public async Task WriteDeviceMetric_PublishesTypedDcmd()
    {
        var result = await _service.WriteDeviceMetricAsync("g", "n", "d1", "Speed", "12.345678901234");

        result.Should().Be(SparkplugCommandResult.Published);
        var message = _published.Should().ContainSingle().Subject;
        message.Topic.Should().Be("spBv1.0/g/DCMD/n/d1");
        message.QualityOfServiceLevel.Should().Be(MqttQualityOfServiceLevel.AtMostOnce);

        var metric = Payload.Parser.ParseFrom(message.GetPayloadSegment().ToArray())
            .Metrics.Should().ContainSingle().Subject;
        metric.Name.Should().Be("Speed");
        metric.Datatype.Should().Be(10u);
        metric.ValueCase.Should().Be(Payload.Types.Metric.ValueOneofCase.DoubleValue);
        metric.DoubleValue.Should().Be(12.345678901234d);
    }

    [Test]
    public async Task WriteNodeMetric_StringValue_ArrivesVerbatim()
    {
        const string json = " {\"a\": [1, 2],\n \"ü\": \"☃\"} ";

        await _service.WriteNodeMetricAsync("g", "n", "Label", json);

        Payload.Parser.ParseFrom(_published.Single().GetPayloadSegment().ToArray())
            .Metrics.Single().StringValue.Should().Be(json);
    }

    [Test]
    public async Task WriteMetric_WhenWritesAreOff_ReturnsNotAllowedAndPublishesNothing()
    {
        _settings.AllowMetricWrites = false;

        (await _service.WriteNodeMetricAsync("g", "n", "Setpoint", "1"))
            .Should().Be(SparkplugCommandResult.NotAllowed);
        (await _service.WriteDeviceMetricAsync("g", "n", "d1", "Speed", "1"))
            .Should().Be(SparkplugCommandResult.NotAllowed);

        AssertNothingPublished();
    }

    [TestCase("Node Control/Reboot")]
    [TestCase("node control/reboot")]
    [TestCase(" Node Control/Reboot ")]
    [TestCase("Node Control/Rebirth")]
    [TestCase("Node Control/Next Server")]
    [TestCase("Node Control/Scan Rate")]
    public async Task WriteNodeMetric_ControlMetric_IsRefusedEvenWhenRebootIsAllowed(string metricName)
    {
        _settings.AllowNodeReboot = true;

        var result = await _service.WriteNodeMetricAsync("g", "n", metricName, "true");

        result.Should().Be(SparkplugCommandResult.NotAllowed);
        AssertNothingPublished();
    }

    [TestCase("Device Control/Reboot")]
    [TestCase("DEVICE CONTROL/REBOOT")]
    [TestCase("Device Control/Rebirth")]
    [TestCase("Device Control/Scan Rate")]
    public async Task WriteDeviceMetric_ControlMetric_IsRefused(string metricName)
    {
        var result = await _service.WriteDeviceMetricAsync("g", "n", "d1", metricName, "true");

        result.Should().Be(SparkplugCommandResult.NotAllowed);
        AssertNothingPublished();
    }

    [Test]
    public async Task WriteMetric_UnknownTarget_ReturnsTargetNotFound()
    {
        (await _service.WriteNodeMetricAsync("missing", "n", "Setpoint", "1"))
            .Should().Be(SparkplugCommandResult.TargetNotFound);
        (await _service.WriteNodeMetricAsync("g", "missing", "Setpoint", "1"))
            .Should().Be(SparkplugCommandResult.TargetNotFound);
        (await _service.WriteDeviceMetricAsync("g", "n", "missing", "Speed", "1"))
            .Should().Be(SparkplugCommandResult.TargetNotFound);

        AssertNothingPublished();
    }

    [Test]
    public async Task WriteMetric_NotInBirthCertificate_ReturnsTargetNotFound()
    {
        (await _service.WriteNodeMetricAsync("g", "n", "DataOnly", "1"))
            .Should().Be(SparkplugCommandResult.TargetNotFound);
        (await _service.WriteNodeMetricAsync("g", "n", "Speed", "1"))
            .Should().Be(SparkplugCommandResult.TargetNotFound);
        (await _service.WriteDeviceMetricAsync("g", "n", "d1", "Setpoint", "1"))
            .Should().Be(SparkplugCommandResult.TargetNotFound);

        AssertNothingPublished();
    }

    [TestCase("Setpoint", "2147483648")]
    [TestCase("Setpoint", "abc")]
    [TestCase("Blob", "AQID")]
    public async Task WriteNodeMetric_InvalidOrUnsupportedValue_ReturnsInvalidValue(string metricName, string value)
    {
        var result = await _service.WriteNodeMetricAsync("g", "n", metricName, value);

        result.Should().Be(SparkplugCommandResult.InvalidValue);
        AssertNothingPublished();
    }

    [Test]
    public async Task WriteMetric_WhenDisconnected_FailsWithoutQueueing()
    {
        _client.TryPublishConnectedAsync(
                Arg.Any<MqttApplicationMessage>(),
                Arg.Any<long?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _service.WriteNodeMetricAsync("g", "n", "Setpoint", "1");

        result.Should().Be(SparkplugCommandResult.Failed);
        await _client.DidNotReceive().EnqueueAsync(Arg.Any<MqttApplicationMessage>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ControlCommands_UseCommandEnvelope()
    {
        var enqueued = new List<MqttApplicationMessage>();
        await _client.EnqueueAsync(Arg.Do<MqttApplicationMessage>(enqueued.Add), Arg.Any<CancellationToken>());
        _settings.AllowNodeReboot = true;

        await _service.RequestNodeRebirthAsync("g", "n");
        await _service.RequestNodeRebootAsync("g", "n");
        await _service.RequestDeviceRebirthAsync("g", "n", "d1");

        enqueued.Should().HaveCount(3);
        enqueued.Select(message => message.Topic).Should().Equal(
            "spBv1.0/g/NCMD/n", "spBv1.0/g/NCMD/n", "spBv1.0/g/DCMD/n/d1");
        foreach (var message in enqueued)
        {
            message.QualityOfServiceLevel.Should().Be(MqttQualityOfServiceLevel.AtMostOnce);
            message.Retain.Should().BeFalse();
            var payload = Payload.Parser.ParseFrom(message.GetPayloadSegment().ToArray());
            payload.Timestamp.Should().Be(1_800_000_000_123UL);
            payload.Seq.Should().Be(0UL, "proto3 omits a zero seq from the wire");
        }
    }

    private void AssertNothingPublished()
    {
        _published.Should().BeEmpty();
        _client.DidNotReceive().EnqueueAsync(Arg.Any<MqttApplicationMessage>(), Arg.Any<CancellationToken>());
    }

    private static MetricSnapshot Ms(string name, string dataType) =>
        new() { Name = name, DataType = dataType, Value = "0" };
}
