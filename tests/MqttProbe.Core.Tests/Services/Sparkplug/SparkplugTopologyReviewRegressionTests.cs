using Google.Protobuf;
using MQTTnet;
using MqttProbe.Core.Models.Sparkplug;
using MqttProbe.Core.Services.Plugins.BuiltIn;
using MqttProbe.Core.Services.Sparkplug;
using MqttProbe.PluginContracts;
using MqttProbe.Tests.Utilities;
using Org.Eclipse.Tahu.Protobuf;

namespace MqttProbe.Core.Tests.Services.Sparkplug;

[TestFixture]
public class SparkplugTopologyReviewRegressionTests
{
    private const string DeviceBirthTopic = "spBv1.0/g/DBIRTH/n/d";
    private const string DeviceDataTopic = "spBv1.0/g/DDATA/n/d";

    private SparkplugMetricMetadataStore _store = null!;
    private SparkplugTopologyExtractor _extractor = null!;
    private SparkplugTopologyService _service = null!;

    [SetUp]
    public void Setup()
    {
        _store = new SparkplugMetricMetadataStore();
        _extractor = new SparkplugTopologyExtractor();
        _service = new SparkplugTopologyService(metricMetadataStore: _store);
    }

    [Test]
    public async Task CompleteObservation_RejectedDuplicateAndStaleDeviceBirths_KeepAcceptedSignedMetadata()
    {
        var observation = new SpbSequenceObservationContext(1, 100, HasCompleteSubscriptions: true);

        await Fire("spBv1.0/g/NBIRTH/n", Payload(1, NamedMetric("Boot", 12, "ok")), observation);
        await Fire(DeviceBirthTopic, Payload(2, NamedMetric("Delta", 1, 5, alias: 42)), observation);
        await Fire(DeviceBirthTopic, Payload(2, NamedMetric("Delta", 5, 206, alias: 42)), observation);
        await Fire(DeviceBirthTopic, Payload(1, NamedMetric("Delta", 5, 206, alias: 42)), observation);

        _store.ResolveDatatype("g", "n", "d", 42, "Delta").Should().Be(1u);
        AssertMetric(DeviceMetrics(), "int8", "5");

        await Fire(DeviceDataTopic, Payload(3, AliasMetric(42, 206)), observation);

        AssertMetric(DeviceMetrics(), "int8", "-50");
    }

    [Test]
    public void ExtractBirth_DoesNotRecordMetadata()
    {
        var birth = Envelope(DeviceBirthTopic, Payload(2, NamedMetric("Delta", 1, 5, alias: 42)));

        _extractor.Extract(birth).Should().ContainSingle().Subject.Should().BeOfType<DeviceBirthEvent>();
        _extractor.Extract(birth, _store).Should().ContainSingle().Subject.Should().BeOfType<DeviceBirthEvent>();

        _store.ResolveName("g", "n", "d", 42).Should().BeNull();
        _store.ResolveDatatype("g", "n", "d", 42, "Delta").Should().BeNull();

        var data = _extractor.Extract(Envelope(DeviceDataTopic, Payload(3, AliasMetric(42, 206))), _store);

        data.Should().ContainSingle().Subject.Should().BeOfType<DeviceDataEvent>()
            .Subject.Metrics.Should().BeEmpty();
    }

    [Test]
    public async Task PipelineBirth_ExtractedButNotApplied_DoesNotRecordMetadata()
    {
        var pipeline = TestPipelineHelper.BuildBuiltInPipeline();
        var dataPayload = Payload(3, AliasMetric(42, 206));

        var birth = pipeline.ProcessInbound(
            MakeArgs(DeviceBirthTopic, Payload(2, NamedMetric("Delta", 1, 5, alias: 42))), _store);
        birth.TopologyEvents.Should().ContainSingle().Subject.Should().BeOfType<DeviceBirthEvent>();
        _store.ResolveDatatype("g", "n", "d", 42, "Delta").Should().BeNull();

        var beforeApply = pipeline.ProcessInbound(MakeArgs(DeviceDataTopic, dataPayload), _store);
        beforeApply.TopologyEvents.Should().ContainSingle().Subject.Should().BeOfType<DeviceDataEvent>()
            .Subject.Metrics.Should().BeEmpty();

        await _service.ApplyTopologyEventsAsync(birth.TopologyEvents);

        _store.ResolveDatatype("g", "n", "d", 42, "Delta").Should().Be(1u);

        var afterApply = pipeline.ProcessInbound(MakeArgs(DeviceDataTopic, dataPayload), _store);
        var metric = ((DeviceDataEvent)afterApply.TopologyEvents.Should().ContainSingle().Subject)
            .Metrics.Should().ContainSingle().Subject;
        metric.DataType.Should().Be("int8");
        metric.Value.Should().Be("-50");
    }

    [Test]
    public async Task IncompleteObservation_DuplicateDeviceBirth_IsStillApplied()
    {
        await Fire(DeviceBirthTopic, Payload(2, NamedMetric("Delta", 1, 5, alias: 42)));
        await Fire(DeviceBirthTopic, Payload(2, NamedMetric("Delta", 5, 206, alias: 42)));

        _store.ResolveDatatype("g", "n", "d", 42, "Delta").Should().Be(5u);
        AssertMetric(DeviceMetrics(), "uint8", "206");

        await Fire(DeviceDataTopic, Payload(3, AliasMetric(42, 206)));

        AssertMetric(DeviceMetrics(), "uint8", "206");
    }

    [Test]
    public async Task DeviceDeath_RetainsAcceptedBirthMetadata()
    {
        await Fire(DeviceBirthTopic, Payload(2, NamedMetric("Delta", 1, 5, alias: 42)));
        await Fire("spBv1.0/g/DDEATH/n/d", Payload(3));
        await Fire(DeviceDataTopic, Payload(4, AliasMetric(42, 206)));

        AssertMetric(DeviceMetrics(), "int8", "-50");
    }

    private IReadOnlyList<SpbMetricSnapshot> DeviceMetrics() =>
        _service.Groups["g"].Nodes["n"].Devices["d"].Metrics;

    private static void AssertMetric(
        IEnumerable<SpbMetricSnapshot> metrics, string dataType, string value)
    {
        var metric = metrics.Should().ContainSingle(snapshot => snapshot.Name == "Delta").Subject;
        metric.DataType.Should().Be(dataType);
        metric.Value.Should().Be(value);
    }

    private async Task<IReadOnlyList<TopologyEvent>> Fire(
        string topic, Payload payload, SpbSequenceObservationContext? observation = null)
    {
        var events = _extractor.Extract(Envelope(topic, payload), _store);
        await _service.ApplyTopologyEventsAsync(events, observation);
        return events;
    }

    private static DecodedPayloadEnvelope Envelope(string topic, Payload payload) =>
        DecodedPayloadEnvelope.CreateSuccess(
            "sparkplug-b", topic, payload.ToByteArray(), payload.ToString(), typedPayload: payload);

    private static MqttApplicationMessageReceivedEventArgs MakeArgs(string topic, Payload payload)
    {
        var appMessage = new MQTTnet.MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload.ToByteArray())
            .Build();
        var packet = new MQTTnet.Packets.MqttPublishPacket { Topic = topic };
        return new MqttApplicationMessageReceivedEventArgs("test-client", appMessage, packet, null);
    }

    private static Payload Payload(ulong sequence, params Payload.Types.Metric[] metrics)
    {
        var payload = new Payload
        {
            Timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Seq = sequence
        };
        payload.Metrics.AddRange(metrics);
        return payload;
    }

    private static Payload.Types.Metric NamedMetric(string name, uint datatype, object value, ulong alias = 0)
    {
        var metric = new Payload.Types.Metric { Name = name, Datatype = datatype, Alias = alias };
        SetValue(metric, value);
        return metric;
    }

    private static Payload.Types.Metric AliasMetric(ulong alias, uint intValue) =>
        new() { Alias = alias, IntValue = intValue };

    private static void SetValue(Payload.Types.Metric metric, object value)
    {
        switch (value)
        {
            case int intValue:
                metric.IntValue = (uint)intValue;
                break;
            case string stringValue:
                metric.StringValue = stringValue;
                break;
            default:
                throw new ArgumentException($"Unsupported metric value type {value.GetType()}.", nameof(value));
        }
    }
}
