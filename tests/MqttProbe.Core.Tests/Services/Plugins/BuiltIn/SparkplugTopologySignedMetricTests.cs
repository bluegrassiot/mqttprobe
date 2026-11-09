using Google.Protobuf;
using MqttProbe.Core.Services.Plugins.BuiltIn;
using MqttProbe.Core.Services.Sparkplug;
using MqttProbe.PluginContracts;
using Org.Eclipse.Tahu.Protobuf;

namespace MqttProbe.Core.Tests.Services.Plugins.BuiltIn;

[TestFixture]
public class SparkplugTopologySignedMetricTests
{
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
    public void Extract_NBirth_SignedIntegerTypes_DecodeSignAndWidth()
    {
        var payload = PayloadWith(
            new Payload.Types.Metric { Name = "MinInt8", Datatype = 1, IntValue = 0x80 },
            new Payload.Types.Metric { Name = "Minus50", Datatype = 1, IntValue = 0xCE },
            new Payload.Types.Metric { Name = "Minus51", Datatype = 1, IntValue = 0xCD },
            new Payload.Types.Metric { Name = "MinusOneInt16", Datatype = 2, IntValue = 0xFFFF },
            new Payload.Types.Metric { Name = "MinInt32", Datatype = 3, IntValue = 0x80000000 },
            new Payload.Types.Metric { Name = "MinInt64", Datatype = 4, LongValue = 0x8000000000000000 });

        var metrics = BirthMetrics(payload);

        AssertMetric(metrics, "MinInt8", "int8", "-128");
        AssertMetric(metrics, "Minus50", "int8", "-50");
        AssertMetric(metrics, "Minus51", "int8", "-51");
        AssertMetric(metrics, "MinusOneInt16", "int16", "-1");
        AssertMetric(metrics, "MinInt32", "int32", "-2147483648");
        AssertMetric(metrics, "MinInt64", "int64", "-9223372036854775808");
    }

    [Test]
    public void Extract_NBirth_SignExtendedRawIntValue_TruncatesToDeclaredWidth()
    {
        var payload = PayloadWith(
            new Payload.Types.Metric { Name = "Int8", Datatype = 1, IntValue = 0xFFFFFFFF },
            new Payload.Types.Metric { Name = "Int16", Datatype = 2, IntValue = 0xFFFFFFFF },
            new Payload.Types.Metric { Name = "UInt8", Datatype = 5, IntValue = 0xFFFFFFFF },
            new Payload.Types.Metric { Name = "UInt16", Datatype = 6, IntValue = 0xFFFFFFFF },
            new Payload.Types.Metric { Name = "UInt32", Datatype = 7, IntValue = 0xFFFFFFFF });

        var metrics = BirthMetrics(payload);

        AssertMetric(metrics, "Int8", "int8", "-1");
        AssertMetric(metrics, "Int16", "int16", "-1");
        AssertMetric(metrics, "UInt8", "uint8", "255");
        AssertMetric(metrics, "UInt16", "uint16", "65535");
        AssertMetric(metrics, "UInt32", "uint32", "4294967295");
    }

    [Test]
    public void Extract_NBirth_UnsignedIntegerTypes_PreserveHighValues()
    {
        var payload = PayloadWith(
            new Payload.Types.Metric { Name = "MaxUInt8", Datatype = 5, IntValue = 255 },
            new Payload.Types.Metric { Name = "MaxUInt16", Datatype = 6, IntValue = 65535 },
            new Payload.Types.Metric { Name = "MaxUInt32", Datatype = 7, IntValue = 4294967295 },
            new Payload.Types.Metric { Name = "MaxUInt64", Datatype = 8, LongValue = ulong.MaxValue });

        var metrics = BirthMetrics(payload);

        AssertMetric(metrics, "MaxUInt8", "uint8", "255");
        AssertMetric(metrics, "MaxUInt16", "uint16", "65535");
        AssertMetric(metrics, "MaxUInt32", "uint32", "4294967295");
        AssertMetric(metrics, "MaxUInt64", "uint64", "18446744073709551615");
    }

    [Test]
    public async Task Extract_NData_NameOnlyWithoutDatatype_InheritsBirthSignedType()
    {
        await ApplyBirth("spBv1.0/g/NBIRTH/n",
            new Payload.Types.Metric { Name = "Delta", Datatype = 1, IntValue = 5 });
        var data = ExtractData("spBv1.0/g/NDATA/n",
            new Payload.Types.Metric { Name = "Delta", IntValue = 0xFFFFFFFF });

        var metric = data.Should().ContainSingle().Subject.Should().BeOfType<NodeDataEvent>()
            .Subject.Metrics.Should().ContainSingle().Subject;
        metric.DataType.Should().Be("int8");
        metric.Value.Should().Be("-1");
    }

    [Test]
    public async Task Extract_NData_AliasOnlyWithoutDatatype_InheritsBirthSignedType()
    {
        await ApplyBirth("spBv1.0/g/NBIRTH/n",
            new Payload.Types.Metric { Name = "Delta", Alias = 42, Datatype = 2, IntValue = 5 });
        var data = ExtractData("spBv1.0/g/NDATA/n",
            new Payload.Types.Metric { Alias = 42, IntValue = 0xFFFF });

        var metric = data.Should().ContainSingle().Subject.Should().BeOfType<NodeDataEvent>()
            .Subject.Metrics.Should().ContainSingle().Subject;
        metric.Name.Should().Be("Delta");
        metric.DataType.Should().Be("int16");
        metric.Value.Should().Be("-1");
    }

    [Test]
    public async Task Extract_DData_NameOnlyWithoutDatatype_InheritsBirthSignedType()
    {
        await ApplyBirth("spBv1.0/g/DBIRTH/n/d",
            new Payload.Types.Metric { Name = "Delta", Datatype = 1, IntValue = 5 });
        var data = ExtractData("spBv1.0/g/DDATA/n/d",
            new Payload.Types.Metric { Name = "Delta", IntValue = 0xCE });

        var metric = data.Should().ContainSingle().Subject.Should().BeOfType<DeviceDataEvent>()
            .Subject.Metrics.Should().ContainSingle().Subject;
        metric.DataType.Should().Be("int8");
        metric.Value.Should().Be("-50");
    }

    [Test]
    public async Task Extract_DData_AliasOnlyWithoutDatatype_InheritsBirthSignedType()
    {
        await ApplyBirth("spBv1.0/g/DBIRTH/n/d",
            new Payload.Types.Metric { Name = "Delta", Alias = 7, Datatype = 4, LongValue = 5 });
        var data = ExtractData("spBv1.0/g/DDATA/n/d",
            new Payload.Types.Metric { Alias = 7, LongValue = 0xFFFFFFFFFFFFFFFF });

        var metric = data.Should().ContainSingle().Subject.Should().BeOfType<DeviceDataEvent>()
            .Subject.Metrics.Should().ContainSingle().Subject;
        metric.Name.Should().Be("Delta");
        metric.DataType.Should().Be("int64");
        metric.Value.Should().Be("-1");
    }

    [Test]
    public async Task Extract_DData_WithoutDeviceBirth_DoesNotInheritNodeBirthType()
    {
        await ApplyBirth("spBv1.0/g/NBIRTH/n",
            new Payload.Types.Metric { Name = "Delta", Alias = 42, Datatype = 1, IntValue = 5 });
        var data = ExtractData("spBv1.0/g/DDATA/n/d",
            new Payload.Types.Metric { Name = "Delta", IntValue = 0xFFFFFFFF });

        var metric = data.Should().ContainSingle().Subject.Should().BeOfType<DeviceDataEvent>()
            .Subject.Metrics.Should().ContainSingle().Subject;
        metric.DataType.Should().BeEmpty();
        metric.Value.Should().Be("4294967295");
    }

    [Test]
    public async Task Extract_NDATA_AliasOnly_DoesNotResolveDeviceAlias()
    {
        await ApplyBirth("spBv1.0/g/DBIRTH/n/d",
            new Payload.Types.Metric { Name = "Delta", Alias = 7, Datatype = 1, IntValue = 5 });
        var data = ExtractData("spBv1.0/g/NDATA/n",
            new Payload.Types.Metric { Alias = 7, IntValue = 0xFFFFFFFF });

        data.Should().ContainSingle().Subject.Should().BeOfType<NodeDataEvent>()
            .Subject.Metrics.Should().BeEmpty();
    }

    [Test]
    public async Task Extract_NBirth_ReplacementChangesDataTypeUsedForLaterAliasData()
    {
        await ApplyBirth("spBv1.0/g/NBIRTH/n",
            new Payload.Types.Metric { Name = "Delta", Alias = 42, Datatype = 1, IntValue = 5 });
        await ApplyBirth("spBv1.0/g/NBIRTH/n",
            new Payload.Types.Metric { Name = "Delta", Alias = 42, Datatype = 5, IntValue = 5 });

        var data = ExtractData("spBv1.0/g/NDATA/n",
            new Payload.Types.Metric { Alias = 42, IntValue = 0xFFFFFFFF });

        var metric = data.Should().ContainSingle().Subject.Should().BeOfType<NodeDataEvent>()
            .Subject.Metrics.Should().ContainSingle().Subject;
        metric.DataType.Should().Be("uint8");
        metric.Value.Should().Be("255");
    }

    [Test]
    public async Task Extract_NDeath_KeepsBirthMetadataForLaterData()
    {
        await ApplyBirth("spBv1.0/g/NBIRTH/n",
            new Payload.Types.Metric { Name = "Delta", Alias = 42, Datatype = 1, IntValue = 5 });
        await ApplyBirth("spBv1.0/g/NDEATH/n");
        var data = ExtractData("spBv1.0/g/NDATA/n",
            new Payload.Types.Metric { Alias = 42, IntValue = 0xCE });

        var metric = data.Should().ContainSingle().Subject.Should().BeOfType<NodeDataEvent>()
            .Subject.Metrics.Should().ContainSingle().Subject;
        metric.DataType.Should().Be("int8");
        metric.Value.Should().Be("-50");
    }

    [TestCase(13, "datetime")]
    [TestCase(14, "text")]
    [TestCase(15, "uuid")]
    [TestCase(16, "dataset")]
    [TestCase(17, "bytes")]
    [TestCase(18, "file")]
    [TestCase(19, "template")]
    [TestCase(20, "propertyset")]
    [TestCase(21, "propertysetlist")]
    [TestCase(22, "int8[]")]
    [TestCase(23, "int16[]")]
    [TestCase(24, "int32[]")]
    [TestCase(25, "int64[]")]
    [TestCase(26, "uint8[]")]
    [TestCase(27, "uint16[]")]
    [TestCase(28, "uint32[]")]
    [TestCase(29, "uint64[]")]
    [TestCase(30, "float[]")]
    [TestCase(31, "double[]")]
    [TestCase(32, "bool[]")]
    [TestCase(33, "string[]")]
    [TestCase(34, "datetime[]")]
    [TestCase(99, "unknown")]
    public void Extract_NBirth_DatatypeLabels_AreCanonical(int datatype, string expected)
    {
        var payload = PayloadWith(new Payload.Types.Metric { Name = "M", Datatype = (uint)datatype });

        BirthMetrics(payload).Should().ContainSingle().Which.DataType.Should().Be(expected);
    }

    [Test]
    public void Extract_NBirth_DatetimeLongValue_PreservesRawInteger()
    {
        var payload = PayloadWith(
            new Payload.Types.Metric { Name = "Timestamp", Datatype = 13, LongValue = 1714050000000 });

        AssertMetric(BirthMetrics(payload), "Timestamp", "datetime", "1714050000000");
    }

    private async Task ApplyBirth(string topic, params Payload.Types.Metric[] metrics) =>
        await _service.ApplyTopologyEventsAsync(_extractor.Extract(Envelope(topic, metrics)));

    private IReadOnlyList<TopologyEvent> ExtractData(string topic, params Payload.Types.Metric[] metrics) =>
        _extractor.Extract(Envelope(topic, metrics), _store);

    private IReadOnlyList<MetricSnapshot> BirthMetrics(Payload payload) =>
        _extractor.Extract(Envelope("spBv1.0/g/NBIRTH/n", payload))
            .Should().ContainSingle().Subject.Should().BeOfType<NodeBirthEvent>()
            .Subject.Metrics;

    private static void AssertMetric(
        IReadOnlyList<MetricSnapshot> metrics, string name, string dataType, string value)
    {
        var metric = metrics.Should().ContainSingle(snapshot => snapshot.Name == name).Subject;
        metric.DataType.Should().Be(dataType);
        metric.Value.Should().Be(value);
    }

    private static DecodedPayloadEnvelope Envelope(string topic, params Payload.Types.Metric[] metrics) =>
        Envelope(topic, PayloadWith(metrics));

    private static DecodedPayloadEnvelope Envelope(string topic, Payload payload) =>
        DecodedPayloadEnvelope.CreateSuccess(
            "sparkplug-b", topic, payload.ToByteArray(), payload.ToString(), typedPayload: payload);

    private static Payload PayloadWith(params Payload.Types.Metric[] metrics)
    {
        var payload = new Payload { Timestamp = 1 };
        payload.Metrics.AddRange(metrics);
        return payload;
    }
}
