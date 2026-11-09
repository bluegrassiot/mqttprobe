using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using MqttProbe.Core.Models.Plugins;
using MqttProbe.Core.Models.Sparkplug;
using MqttProbe.Core.Services.Plugins;
using MqttProbe.Core.Services.Plugins.BuiltIn;
using MqttProbe.Core.Services.Sparkplug;
using MqttProbe.PluginContracts;
using Org.Eclipse.Tahu.Protobuf;

namespace MqttProbe.Core.Tests.Services.Sparkplug;

[TestFixture]
public class SparkplugTopologyMetadataLifecycleTests
{
    private SparkplugMetricMetadataStore _store = null!;
    private SparkplugTopologyExtractor _extractor = null!;
    private SparkplugTopologyService _service = null!;

    [SetUp]
    public void Setup()
    {
        _store = new SparkplugMetricMetadataStore();
        _extractor = new SparkplugTopologyExtractor(_store);
        _service = new SparkplugTopologyService(metricMetadataStore: _store);
    }

    [Test]
    public async Task ClearAll_BrokerReset_DropsNamedBirthMetadata()
    {
        await Fire("spBv1.0/g/NBIRTH/n", Payload(1, NamedMetric("Delta", 1, 5)));
        _service.ClearAll();

        await Fire("spBv1.0/g/NDATA/n", Payload(2, DatatypeOmittedMetric("Delta", 0xCE)));

        AssertMetric(_service.Groups["g"].Nodes["n"].Metrics, "Delta", "unknown", "206");
    }

    [Test]
    public async Task ClearAll_BrokerReset_DropsAliasBirthMetadata()
    {
        await Fire("spBv1.0/g/NBIRTH/n", Payload(1, NamedMetric("Delta", 1, 5, alias: 42)));
        _service.ClearAll();

        var data = await Fire("spBv1.0/g/NDATA/n", Payload(2, AliasMetric(42, 0xCE)));

        data.Should().ContainSingle().Subject.Should().BeOfType<NodeDataEvent>()
            .Subject.Metrics.Should().BeEmpty();
    }

    [Test]
    public async Task RemoveNode_DropsBirthMetadata()
    {
        await Fire("spBv1.0/g/NBIRTH/n", Payload(1, NamedMetric("Delta", 1, 5)));
        _service.RemoveNode("g", "n").Should().BeTrue();

        await Fire("spBv1.0/g/NDATA/n", Payload(2, DatatypeOmittedMetric("Delta", 0xCE)));

        AssertMetric(_service.Groups["g"].Nodes["n"].Metrics, "Delta", "unknown", "206");
    }

    [Test]
    public async Task RemoveOfflineNodes_DropsBirthMetadata()
    {
        await Fire("spBv1.0/g/NBIRTH/n", Payload(1, NamedMetric("Delta", 1, 5)));
        await Fire("spBv1.0/g/NDEATH/n", Payload(2));
        _service.RemoveOfflineNodes().Should().Be(1);

        await Fire("spBv1.0/g/NDATA/n", Payload(3, DatatypeOmittedMetric("Delta", 0xCE)));

        AssertMetric(_service.Groups["g"].Nodes["n"].Metrics, "Delta", "unknown", "206");
    }

    [Test]
    public async Task RemoveMatchingTopic_DeviceRemoval_DropsOnlyThatDeviceMetadata()
    {
        await Fire("spBv1.0/g/DBIRTH/n/d", Payload(1, NamedMetric("Delta", 1, 5)));
        await Fire("spBv1.0/g/DBIRTH/n/e", Payload(2, NamedMetric("Delta", 1, 5)));
        _service.RemoveMatchingTopic("spBv1.0/g/DDATA/n/d").Should().Be(1);

        await Fire("spBv1.0/g/DDATA/n/d", Payload(3, DatatypeOmittedMetric("Delta", 0xCE)));
        await Fire("spBv1.0/g/DDATA/n/e", Payload(4, DatatypeOmittedMetric("Delta", 0xCE)));

        var node = _service.Groups["g"].Nodes["n"];
        AssertMetric(node.Devices["d"].Metrics, "Delta", "unknown", "206");
        AssertMetric(node.Devices["e"].Metrics, "Delta", "int8", "-50");
    }

    [Test]
    public async Task RemoveMatchingTopic_NodeRemoval_DropsNodeAndDeviceMetadata()
    {
        await Fire("spBv1.0/g/NBIRTH/n", Payload(1, NamedMetric("Delta", 1, 5)));
        await Fire("spBv1.0/g/DBIRTH/n/d", Payload(2, NamedMetric("Delta", 1, 5)));
        _service.RemoveMatchingTopic("spBv1.0/g/NBIRTH/n").Should().BeGreaterThan(0);

        await Fire("spBv1.0/g/NBIRTH/n", Payload(3, NamedMetric("Other", 12, "x")));
        await Fire("spBv1.0/g/NDATA/n", Payload(4, DatatypeOmittedMetric("Delta", 0xCE)));
        await Fire("spBv1.0/g/DDATA/n/d", Payload(5, DatatypeOmittedMetric("Delta", 0xCE)));

        var node = _service.Groups["g"].Nodes["n"];
        AssertMetric(node.Metrics, "Delta", "unknown", "206");
        AssertMetric(node.Devices["d"].Metrics, "Delta", "unknown", "206");
    }

    [Test]
    public async Task NDeath_KeepsBirthMetadata()
    {
        await Fire("spBv1.0/g/NBIRTH/n", Payload(1, NamedMetric("Delta", 1, 5)));
        await Fire("spBv1.0/g/NDEATH/n", Payload(2));

        await Fire("spBv1.0/g/NDATA/n", Payload(3, DatatypeOmittedMetric("Delta", 0xCE)));

        AssertMetric(_service.Groups["g"].Nodes["n"].Metrics, "Delta", "int8", "-50");
    }

    [Test]
    public async Task DDeath_KeepsBirthMetadata()
    {
        await Fire("spBv1.0/g/DBIRTH/n/d", Payload(1, NamedMetric("Delta", 1, 5)));
        await Fire("spBv1.0/g/DDEATH/n/d", Payload(2));

        await Fire("spBv1.0/g/DDATA/n/d", Payload(3, DatatypeOmittedMetric("Delta", 0xCE)));

        AssertMetric(_service.Groups["g"].Nodes["n"].Devices["d"].Metrics, "Delta", "int8", "-50");
    }

    [Test]
    public void BuildPluginRegistry_SharesMetadataStoreWithBuiltInExtractor()
    {
        var registry = MqttProbePluginStartup.BuildPluginRegistry(
            new PluginConfig(), NullLoggerFactory.Instance, metricMetadataStore: _store);
        var extractor = registry.TopologyExtractors["sparkplug-b"];

        extractor.Extract(Envelope("spBv1.0/g/NBIRTH/n", Payload(1, NamedMetric("Delta", 1, 5))));
        _store.ClearAll();
        var data = extractor.Extract(Envelope("spBv1.0/g/NDATA/n", Payload(2, DatatypeOmittedMetric("Delta", 0xCE))));

        var metric = data.Should().ContainSingle().Subject.Should().BeOfType<NodeDataEvent>()
            .Subject.Metrics.Should().ContainSingle().Subject;
        metric.DataType.Should().BeEmpty();
        metric.Value.Should().Be("206");
    }

    private static DecodedPayloadEnvelope Envelope(string topic, Payload payload) =>
        DecodedPayloadEnvelope.CreateSuccess(
            "sparkplug-b", topic, payload.ToByteArray(), payload.ToString(), typedPayload: payload);

    private static void AssertMetric(
        IEnumerable<SpbMetricSnapshot> metrics, string name, string dataType, string value)
    {
        var metric = metrics.Should().ContainSingle(snapshot => snapshot.Name == name).Subject;
        metric.DataType.Should().Be(dataType);
        metric.Value.Should().Be(value);
    }

    private async Task<IReadOnlyList<TopologyEvent>> Fire(string topic, Payload payload)
    {
        var envelope = DecodedPayloadEnvelope.CreateSuccess(
            "sparkplug-b", topic, payload.ToByteArray(), payload.ToString(), typedPayload: payload);
        var events = _extractor.Extract(envelope);
        await _service.ApplyTopologyEventsAsync(events);
        return events;
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

    private static Payload.Types.Metric DatatypeOmittedMetric(string name, uint intValue) =>
        new() { Name = name, IntValue = intValue };

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
