using Google.Protobuf;
using MqttProbe.Core.Models.Sparkplug;
using MqttProbe.Core.Services.Plugins.BuiltIn;
using MqttProbe.Core.Services.Sparkplug;
using MqttProbe.PluginContracts;
using Org.Eclipse.Tahu.Protobuf;

namespace MqttProbe.Core.Tests.Services.Sparkplug;

[TestFixture]
public class SparkplugTopologyDatatypeRetentionTests
{
    private SparkplugTopologyService _service = null!;
    private SparkplugTopologyExtractor _extractor = null!;

    [SetUp]
    public void Setup()
    {
        _extractor = new SparkplugTopologyExtractor();
        _service = new SparkplugTopologyService();
    }

    [Test]
    public async Task NodeData_AliasOnlyUpdate_WithoutDatatype_RetainsBirthDoubleTypeAndNewValue()
    {
        await Fire("spBv1.0/g/NBIRTH/n", SpbPayload(1, NamedMetric("Pressure", 10, 1.0, 42)));
        await Fire("spBv1.0/g/NDATA/n", SpbPayload(2, AliasMetric(42, 3.5)));

        var metric = _service.Groups["g"].Nodes["n"].Metrics
            .Should().ContainSingle(snapshot => snapshot.Name == "Pressure").Subject;
        metric.DataType.Should().Be("double");
        metric.Value.Should().Be("3.5000");
        metric.Alias.Should().Be(42UL);
    }

    [Test]
    public async Task NodeData_AliasOnlyUpdates_WithoutDatatype_RetainNumericBooleanStringAndDatetimeTypes()
    {
        await Fire("spBv1.0/g/NBIRTH/n", SpbPayload(1,
            NamedMetric("Temperature", 10, 21.5, 42),
            NamedMetric("Ready", 11, true, 43),
            NamedMetric("Label", 12, "42", 44),
            NamedMetric("Timestamp", 14, 1714050000000L, 45)));
        await Fire("spBv1.0/g/NDATA/n", SpbPayload(2,
            AliasMetric(42, 22.75),
            AliasMetric(43, false),
            AliasMetric(44, "true"),
            AliasMetric(45, 1714050001000L)));

        var metrics = _service.Groups["g"].Nodes["n"].Metrics;
        metrics.Should().HaveCount(4);
        AssertMetric(metrics, "Temperature", "double", "22.7500");
        AssertMetric(metrics, "Ready", "bool", "false");
        AssertMetric(metrics, "Label", "string", "true");
        AssertMetric(metrics, "Timestamp", "datetime", "1714050001000");
    }

    [Test]
    public async Task DeviceData_AliasOnlyUpdates_WithoutDatatype_RetainBirthTypesAndValues()
    {
        await Fire("spBv1.0/g/DBIRTH/n/d", SpbPayload(1,
            NamedMetric("Temperature", 10, 21.5, 142),
            NamedMetric("Ready", 11, true, 143),
            NamedMetric("Label", 12, "42", 144),
            NamedMetric("Timestamp", 14, 1714050000000L, 145)));
        await Fire("spBv1.0/g/DDATA/n/d", SpbPayload(2,
            AliasMetric(142, 22.75),
            AliasMetric(143, false),
            AliasMetric(144, "true"),
            AliasMetric(145, 1714050001000L)));

        var metrics = _service.Groups["g"].Nodes["n"].Devices["d"].Metrics;
        metrics.Should().HaveCount(4);
        AssertMetric(metrics, "Temperature", "double", "22.7500");
        AssertMetric(metrics, "Ready", "bool", "false");
        AssertMetric(metrics, "Label", "string", "true");
        AssertMetric(metrics, "Timestamp", "datetime", "1714050001000");
    }

    [Test]
    public async Task NodeData_UnsupportedNonzeroDatatype_DoesNotRetainBirthType()
    {
        await Fire("spBv1.0/g/NBIRTH/n", SpbPayload(1, NamedMetric("Pressure", 10, 1.0, 42)));
        await Fire("spBv1.0/g/NDATA/n", SpbPayload(2, AliasMetric(42, 2.0, 99)));

        AssertMetric(_service.Groups["g"].Nodes["n"].Metrics, "Pressure", "unknown", "2.0000");
    }

    [Test]
    public async Task NodeData_WithoutBirth_MissingDatatypeRemainsUnknown()
    {
        await Fire("spBv1.0/g/NDATA/n", SpbPayload(1, NamedMetric("Pressure", 0, 3.0)));

        AssertMetric(_service.Groups["g"].Nodes["n"].Metrics, "Pressure", "unknown", "3.0000");
    }

    [Test]
    public async Task NodeBirth_ReplacesPreviousMetricTypeAndValue()
    {
        await Fire("spBv1.0/g/NBIRTH/n", SpbPayload(1, NamedMetric("Temperature", 10, 1.0, 42)));
        await Fire("spBv1.0/g/NDATA/n", SpbPayload(2, AliasMetric(42, 2.0)));
        await Fire("spBv1.0/g/NBIRTH/n", SpbPayload(3, NamedMetric("Temperature", 12, "replaced", 42)));

        var metrics = _service.Groups["g"].Nodes["n"].Metrics;
        metrics.Should().ContainSingle();
        AssertMetric(metrics, "Temperature", "string", "replaced");
    }

    [TestCase("NBIRTH")]
    [TestCase("DBIRTH")]
    public async Task Birth_WithoutDatatype_ShowsUnknown(string verb)
    {
        await FireBirth(verb, 1, NamedMetric("Pressure", 0, 1.0));

        AssertMetric(BirthMetrics(verb), "Pressure", "unknown", "1.0000");
    }

    [TestCase("NBIRTH")]
    [TestCase("DBIRTH")]
    public async Task LaterBirth_WithoutDatatype_ReplacesKnownTypeWithUnknown(string verb)
    {
        await FireBirth(verb, 1, NamedMetric("Pressure", 10, 1.0));
        await FireBirth(verb, 2, NamedMetric("Pressure", 0, 3.5));

        AssertMetric(BirthMetrics(verb), "Pressure", "unknown", "3.5000");
    }

    private Task FireBirth(string verb, ulong sequence, params Payload.Types.Metric[] metrics) =>
        Fire(
            $"spBv1.0/g/{verb}/{(verb == "NBIRTH" ? "n" : "n/d")}",
            SpbPayload(sequence, metrics));

    private IReadOnlyList<SpbMetricSnapshot> BirthMetrics(string verb) =>
        verb == "NBIRTH"
            ? _service.Groups["g"].Nodes["n"].Metrics
            : _service.Groups["g"].Nodes["n"].Devices["d"].Metrics;

    private static void AssertMetric(
        IEnumerable<SpbMetricSnapshot> metrics,
        string name,
        string dataType,
        string value)
    {
        var metric = metrics.Should().ContainSingle(snapshot => snapshot.Name == name).Subject;
        metric.DataType.Should().Be(dataType);
        metric.Value.Should().Be(value);
    }

    private Task Fire(string topic, byte[] payload)
    {
        Payload? parsed;
        try
        {
            parsed = Payload.Parser.ParseFrom(payload);
        }
        catch
        {
            parsed = null;
        }

        var envelope = parsed is null
            ? DecodedPayloadEnvelope.CreateFailure("sparkplug-b", topic, payload, "parse failed")
            : DecodedPayloadEnvelope.CreateSuccess(
                "sparkplug-b", topic, payload, parsed.ToString(), typedPayload: parsed);

        return _service.ApplyTopologyEventsAsync(_extractor.Extract(envelope));
    }

    private static byte[] SpbPayload(ulong sequence, params Payload.Types.Metric[] metrics)
    {
        var payload = new Payload { Timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Seq = sequence };
        payload.Metrics.AddRange(metrics);
        return payload.ToByteArray();
    }

    private static Payload.Types.Metric NamedMetric(string name, uint datatype, object value, ulong alias = 0)
    {
        var metric = new Payload.Types.Metric { Name = name, Datatype = datatype, Alias = alias };
        SetValue(metric, value);
        return metric;
    }

    private static Payload.Types.Metric AliasMetric(ulong alias, object value, uint datatype = 0)
    {
        var metric = new Payload.Types.Metric { Alias = alias, Datatype = datatype };
        SetValue(metric, value);
        return metric;
    }

    private static void SetValue(Payload.Types.Metric metric, object value)
    {
        switch (value)
        {
            case double doubleValue:
                metric.DoubleValue = doubleValue;
                break;
            case bool booleanValue:
                metric.BooleanValue = booleanValue;
                break;
            case string stringValue:
                metric.StringValue = stringValue;
                break;
            case long longValue:
                metric.LongValue = (ulong)longValue;
                break;
            default:
                throw new ArgumentException($"Unsupported metric value type {value.GetType()}.", nameof(value));
        }
    }
}
