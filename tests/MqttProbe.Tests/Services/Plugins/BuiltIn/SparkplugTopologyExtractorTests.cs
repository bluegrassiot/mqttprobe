using Google.Protobuf;
using MqttProbe.Services.Plugins.BuiltIn;
using MqttProbe.Services.Plugins.Contracts;
using Org.Eclipse.Tahu.Protobuf;

namespace MqttProbe.Tests.Services.Plugins.BuiltIn;

[TestFixture]
public class SparkplugTopologyExtractorTests
{
    private static DecodedPayloadEnvelope MakeEnvelope(string topic, Payload payload)
    {
        return DecodedPayloadEnvelope.CreateSuccess(
            "sparkplug-b",
            topic,
            payload.ToByteArray(),
            payload.ToString(),
            typedPayload: payload);
    }

    private static DecodedPayloadEnvelope MakeEnvelopeNoTypedPayload(string topic)
    {
        return DecodedPayloadEnvelope.CreateSuccess(
            "sparkplug-b",
            topic,
            [],
            "some display");
    }

    private static DecodedPayloadEnvelope MakeFailureEnvelope(string topic)
    {
        return DecodedPayloadEnvelope.CreateFailure(
            "sparkplug-b",
            topic,
            [],
            "parse failed");
    }

    private static DecodedPayloadEnvelope MakeEnvelopeWrongFormat(string topic, Payload payload)
    {
        return DecodedPayloadEnvelope.CreateSuccess(
            "json",
            topic,
            payload.ToByteArray(),
            payload.ToString(),
            typedPayload: payload);
    }

    private static Payload MakePayloadWithMetrics(params Payload.Types.Metric[] metrics)
    {
        var payload = new Payload
        {
            Timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        payload.Metrics.AddRange(metrics);
        return payload;
    }

    private static Payload.Types.Metric MakeIntMetric(string name, uint value) =>
        new() { Name = name, Datatype = 3, IntValue = value };

    private static Payload.Types.Metric MakeStringMetric(string name, string value) =>
        new() { Name = name, Datatype = 12, StringValue = value };

    private static Payload.Types.Metric MakeBoolMetric(string name, bool value) =>
        new() { Name = name, Datatype = 11, BooleanValue = value };

    private static Payload.Types.Metric MakeDoubleMetric(string name, double value) =>
        new() { Name = name, Datatype = 10, DoubleValue = value };

    private static Payload.Types.Metric MakeFloatMetric(string name, float value) =>
        new() { Name = name, Datatype = 9, FloatValue = value };

    private static Payload.Types.Metric MakeLongMetric(string name, ulong value) =>
        new() { Name = name, Datatype = 4, LongValue = value };

    // --- FormatId ---

    [Test]
    public void FormatId_IsSparkplugB()
    {
        new SparkplugTopologyExtractor().FormatId.Should().Be("sparkplug-b");
    }

    // --- NBIRTH ---

    [Test]
    public void Extract_NBirth_ReturnsNodeBirthEvent()
    {
        var extractor = new SparkplugTopologyExtractor();
        var payload = MakePayloadWithMetrics(
            MakeIntMetric("Temperature", 42),
            MakeStringMetric("Status", "ok"));
        var envelope = MakeEnvelope("spBv1.0/mygroup/NBIRTH/eon1", payload);

        var events = extractor.Extract(envelope);

        events.Should().HaveCount(1);
        var evt = events[0].Should().BeOfType<NodeBirthEvent>().Subject;
        evt.GroupId.Should().Be("mygroup");
        evt.NodeId.Should().Be("eon1");
        evt.FormatId.Should().Be("sparkplug-b");
        evt.Topic.Should().Be("spBv1.0/mygroup/NBIRTH/eon1");
        evt.Metrics.Should().HaveCount(2);
        evt.Metrics[0].Name.Should().Be("Temperature");
        evt.Metrics[0].Value.Should().Be("42");
        evt.Metrics[0].DataType.Should().Be("int32");
        evt.Metrics[1].Name.Should().Be("Status");
        evt.Metrics[1].Value.Should().Be("ok");
        evt.Metrics[1].DataType.Should().Be("string");
    }

    // --- NDEATH ---

    [Test]
    public void Extract_NDeath_ReturnsNodeDeathEvent()
    {
        var extractor = new SparkplugTopologyExtractor();
        var payload = new Payload { Timestamp = 1 };
        var envelope = MakeEnvelope("spBv1.0/mygroup/NDEATH/eon1", payload);

        var events = extractor.Extract(envelope);

        events.Should().HaveCount(1);
        var evt = events[0].Should().BeOfType<NodeDeathEvent>().Subject;
        evt.GroupId.Should().Be("mygroup");
        evt.NodeId.Should().Be("eon1");
        evt.FormatId.Should().Be("sparkplug-b");
        evt.Topic.Should().Be("spBv1.0/mygroup/NDEATH/eon1");
    }

    // --- NDATA ---

    [Test]
    public void Extract_NData_ReturnsNodeDataEvent()
    {
        var extractor = new SparkplugTopologyExtractor();
        var payload = MakePayloadWithMetrics(MakeIntMetric("Pressure", 101));
        var envelope = MakeEnvelope("spBv1.0/mygroup/NDATA/eon1", payload);

        var events = extractor.Extract(envelope);

        events.Should().HaveCount(1);
        var evt = events[0].Should().BeOfType<NodeDataEvent>().Subject;
        evt.GroupId.Should().Be("mygroup");
        evt.NodeId.Should().Be("eon1");
        evt.Metrics.Should().HaveCount(1);
        evt.Metrics[0].Name.Should().Be("Pressure");
        evt.Metrics[0].Value.Should().Be("101");
    }

    // --- DBIRTH ---

    [Test]
    public void Extract_DBirth_ReturnsDeviceBirthEvent()
    {
        var extractor = new SparkplugTopologyExtractor();
        var payload = MakePayloadWithMetrics(
            MakeStringMetric("Device/Model", "XYZ"),
            MakeBoolMetric("Device/Online", true));
        var envelope = MakeEnvelope("spBv1.0/mygroup/DBIRTH/eon1/device1", payload);

        var events = extractor.Extract(envelope);

        events.Should().HaveCount(1);
        var evt = events[0].Should().BeOfType<DeviceBirthEvent>().Subject;
        evt.GroupId.Should().Be("mygroup");
        evt.NodeId.Should().Be("eon1");
        evt.DeviceId.Should().Be("device1");
        evt.FormatId.Should().Be("sparkplug-b");
        evt.Topic.Should().Be("spBv1.0/mygroup/DBIRTH/eon1/device1");
        evt.Metrics.Should().HaveCount(2);
        evt.Metrics[0].Name.Should().Be("Device/Model");
        evt.Metrics[0].Value.Should().Be("XYZ");
        evt.Metrics[1].Name.Should().Be("Device/Online");
        evt.Metrics[1].Value.Should().Be("true");
    }

    // --- DDEATH ---

    [Test]
    public void Extract_DDeath_ReturnsDeviceDeathEvent()
    {
        var extractor = new SparkplugTopologyExtractor();
        var payload = new Payload { Timestamp = 1 };
        var envelope = MakeEnvelope("spBv1.0/mygroup/DDEATH/eon1/device1", payload);

        var events = extractor.Extract(envelope);

        events.Should().HaveCount(1);
        var evt = events[0].Should().BeOfType<DeviceDeathEvent>().Subject;
        evt.GroupId.Should().Be("mygroup");
        evt.NodeId.Should().Be("eon1");
        evt.DeviceId.Should().Be("device1");
        evt.FormatId.Should().Be("sparkplug-b");
        evt.Topic.Should().Be("spBv1.0/mygroup/DDEATH/eon1/device1");
    }

    // --- DDATA ---

    [Test]
    public void Extract_DData_ReturnsDeviceDataEvent()
    {
        var extractor = new SparkplugTopologyExtractor();
        var payload = MakePayloadWithMetrics(MakeDoubleMetric("Temp", 22.5));
        var envelope = MakeEnvelope("spBv1.0/mygroup/DDATA/eon1/device1", payload);

        var events = extractor.Extract(envelope);

        events.Should().HaveCount(1);
        var evt = events[0].Should().BeOfType<DeviceDataEvent>().Subject;
        evt.GroupId.Should().Be("mygroup");
        evt.NodeId.Should().Be("eon1");
        evt.DeviceId.Should().Be("device1");
        evt.Metrics.Should().HaveCount(1);
        evt.Metrics[0].Name.Should().Be("Temp");
        evt.Metrics[0].Value.Should().Be("22.5000");
    }

    // --- STATE topic returns empty ---

    [Test]
    public void Extract_STATE_ReturnsEmpty()
    {
        var extractor = new SparkplugTopologyExtractor();
        var payload = new Payload { Timestamp = 1 };
        var envelope = MakeEnvelope("spBv1.0/STATE/eon1", payload);

        var events = extractor.Extract(envelope);

        events.Should().BeEmpty();
    }

    // --- Missing TypedPayload returns empty ---

    [Test]
    public void Extract_MissingTypedPayload_ReturnsEmpty()
    {
        var extractor = new SparkplugTopologyExtractor();
        var envelope = MakeEnvelopeNoTypedPayload("spBv1.0/mygroup/NBIRTH/eon1");

        var events = extractor.Extract(envelope);

        events.Should().BeEmpty();
    }

    // --- Failure envelope returns empty ---

    [Test]
    public void Extract_FailureEnvelope_ReturnsEmpty()
    {
        var extractor = new SparkplugTopologyExtractor();
        var envelope = MakeFailureEnvelope("spBv1.0/mygroup/NBIRTH/eon1");

        var events = extractor.Extract(envelope);

        events.Should().BeEmpty();
    }

    // --- Wrong format returns empty ---

    [Test]
    public void Extract_WrongFormat_ReturnsEmpty()
    {
        var extractor = new SparkplugTopologyExtractor();
        var payload = MakePayloadWithMetrics(MakeIntMetric("x", 1));
        var envelope = MakeEnvelopeWrongFormat("spBv1.0/mygroup/NBIRTH/eon1", payload);

        var events = extractor.Extract(envelope);

        events.Should().BeEmpty();
    }

    // --- Malformed topic returns empty ---

    [Test]
    public void Extract_MalformedTopic_ReturnsEmpty()
    {
        var extractor = new SparkplugTopologyExtractor();
        var payload = new Payload { Timestamp = 1 };
        var envelope = MakeEnvelope("not-sparkplug/group/NBIRTH/eon1", payload);

        var events = extractor.Extract(envelope);

        events.Should().BeEmpty();
    }

    [Test]
    public void Extract_TooShortTopic_ReturnsEmpty()
    {
        var extractor = new SparkplugTopologyExtractor();
        var payload = new Payload { Timestamp = 1 };
        var envelope = MakeEnvelope("spBv1.0/group", payload);

        var events = extractor.Extract(envelope);

        events.Should().BeEmpty();
    }

    // --- Metric extraction matches ExtractMetricValue ---

    [Test]
    public void Extract_IntMetric_FormatsCorrectly()
    {
        var extractor = new SparkplugTopologyExtractor();
        var payload = MakePayloadWithMetrics(MakeIntMetric("val", 42));
        var envelope = MakeEnvelope("spBv1.0/g/NBIRTH/n", payload);

        var metric = extractor.Extract(envelope)[0].Should().BeOfType<NodeBirthEvent>()
            .Subject.Metrics[0];
        metric.DataType.Should().Be("int32");
        metric.Value.Should().Be("42");
    }

    [Test]
    public void Extract_LongMetric_FormatsCorrectly()
    {
        var extractor = new SparkplugTopologyExtractor();
        var payload = MakePayloadWithMetrics(MakeLongMetric("val", 9999999999L));
        var envelope = MakeEnvelope("spBv1.0/g/NBIRTH/n", payload);

        var metric = extractor.Extract(envelope)[0].Should().BeOfType<NodeBirthEvent>()
            .Subject.Metrics[0];
        metric.DataType.Should().Be("int64");
        metric.Value.Should().Be("9999999999");
    }

    [Test]
    public void Extract_FloatMetric_FormatsCorrectly()
    {
        var extractor = new SparkplugTopologyExtractor();
        var payload = MakePayloadWithMetrics(MakeFloatMetric("val", 1.5f));
        var envelope = MakeEnvelope("spBv1.0/g/NBIRTH/n", payload);

        var metric = extractor.Extract(envelope)[0].Should().BeOfType<NodeBirthEvent>()
            .Subject.Metrics[0];
        metric.DataType.Should().Be("float");
        metric.Value.Should().Be("1.5000");
    }

    [Test]
    public void Extract_DoubleMetric_FormatsCorrectly()
    {
        var extractor = new SparkplugTopologyExtractor();
        var payload = MakePayloadWithMetrics(MakeDoubleMetric("val", 3.14159));
        var envelope = MakeEnvelope("spBv1.0/g/NBIRTH/n", payload);

        var metric = extractor.Extract(envelope)[0].Should().BeOfType<NodeBirthEvent>()
            .Subject.Metrics[0];
        metric.DataType.Should().Be("double");
        metric.Value.Should().Be("3.1416");
    }

    [Test]
    public void Extract_BoolMetric_FormatsCorrectly()
    {
        var extractor = new SparkplugTopologyExtractor();
        var payload = MakePayloadWithMetrics(MakeBoolMetric("val", false));
        var envelope = MakeEnvelope("spBv1.0/g/NBIRTH/n", payload);

        var metric = extractor.Extract(envelope)[0].Should().BeOfType<NodeBirthEvent>()
            .Subject.Metrics[0];
        metric.DataType.Should().Be("bool");
        metric.Value.Should().Be("false");
    }

    [Test]
    public void Extract_StringMetric_FormatsCorrectly()
    {
        var extractor = new SparkplugTopologyExtractor();
        var payload = MakePayloadWithMetrics(MakeStringMetric("val", "hello"));
        var envelope = MakeEnvelope("spBv1.0/g/NBIRTH/n", payload);

        var metric = extractor.Extract(envelope)[0].Should().BeOfType<NodeBirthEvent>()
            .Subject.Metrics[0];
        metric.DataType.Should().Be("string");
        metric.Value.Should().Be("hello");
    }

    [Test]
    public void Extract_UnknownDatatype_ShowsUnknown()
    {
        var extractor = new SparkplugTopologyExtractor();
        var metric = new Payload.Types.Metric { Name = "val", Datatype = 99 };
        var payload = MakePayloadWithMetrics(metric);
        var envelope = MakeEnvelope("spBv1.0/g/NBIRTH/n", payload);

        var result = extractor.Extract(envelope)[0].Should().BeOfType<NodeBirthEvent>()
            .Subject.Metrics[0];
        result.DataType.Should().Be("unknown");
    }

    [Test]
    public void Extract_MetricWithNoValueCase_ShowsDash()
    {
        var extractor = new SparkplugTopologyExtractor();
        // Metric with only name and datatype, no value set
        var metric = new Payload.Types.Metric { Name = "val", Datatype = 3 };
        var payload = MakePayloadWithMetrics(metric);
        var envelope = MakeEnvelope("spBv1.0/g/NBIRTH/n", payload);

        var result = extractor.Extract(envelope)[0].Should().BeOfType<NodeBirthEvent>()
            .Subject.Metrics[0];
        result.Value.Should().Be("\u2014");
    }

    [Test]
    public void Extract_NBirth_AliasWithoutName_IsSkipped()
    {
        var extractor = new SparkplugTopologyExtractor();
        // NBIRTH metric with alias but no name — cannot establish mapping, skip.
        var metric = new Payload.Types.Metric { Alias = 42, Datatype = 3, IntValue = 7 };
        var payload = MakePayloadWithMetrics(metric);
        var envelope = MakeEnvelope("spBv1.0/g/NBIRTH/n", payload);

        var result = extractor.Extract(envelope);
        result[0].Should().BeOfType<NodeBirthEvent>().Subject.Metrics.Should().BeEmpty();
    }

    [Test]
    public void Extract_NData_AliasResolvedFromNBirth()
    {
        var extractor = new SparkplugTopologyExtractor();

        // NBIRTH establishes alias 10 -> "Temperature"
        var birthPayload = MakePayloadWithMetrics(
            new Payload.Types.Metric { Name = "Temperature", Alias = 10, Datatype = 3, IntValue = 42 });
        extractor.Extract(MakeEnvelope("spBv1.0/mygroup/NBIRTH/eon1", birthPayload));

        // NDATA sends only alias 10 with updated value
        var dataPayload = MakePayloadWithMetrics(
            new Payload.Types.Metric { Alias = 10, Datatype = 3, IntValue = 99 });
        var events = extractor.Extract(MakeEnvelope("spBv1.0/mygroup/NDATA/eon1", dataPayload));

        events.Should().HaveCount(1);
        var evt = events[0].Should().BeOfType<NodeDataEvent>().Subject;
        evt.Metrics.Should().HaveCount(1);
        evt.Metrics[0].Name.Should().Be("Temperature");
        evt.Metrics[0].Value.Should().Be("99");
    }

    [Test]
    public void Extract_DData_AliasResolvedFromDBirth()
    {
        var extractor = new SparkplugTopologyExtractor();

        // DBIRTH establishes alias 20 -> "Pressure"
        var birthPayload = MakePayloadWithMetrics(
            new Payload.Types.Metric { Name = "Pressure", Alias = 20, Datatype = 10, DoubleValue = 101.3 });
        extractor.Extract(MakeEnvelope("spBv1.0/mygroup/DBIRTH/eon1/device1", birthPayload));

        // DDATA sends only alias 20
        var dataPayload = MakePayloadWithMetrics(
            new Payload.Types.Metric { Alias = 20, Datatype = 10, DoubleValue = 102.5 });
        var events = extractor.Extract(MakeEnvelope("spBv1.0/mygroup/DDATA/eon1/device1", dataPayload));

        events.Should().HaveCount(1);
        var evt = events[0].Should().BeOfType<DeviceDataEvent>().Subject;
        evt.Metrics.Should().HaveCount(1);
        evt.Metrics[0].Name.Should().Be("Pressure");
        evt.Metrics[0].Value.Should().Be("102.5000");
    }

    [Test]
    public void Extract_NData_AliasOnly_ColdStart_Skipped()
    {
        // Cold start: NDATA arrives with alias-only metric before any NBIRTH
        // established that alias. No cached name exists — metric is skipped.
        var extractor = new SparkplugTopologyExtractor();

        var dataPayload = MakePayloadWithMetrics(
            new Payload.Types.Metric { Alias = 10, Datatype = 3, IntValue = 99 });
        var events = extractor.Extract(MakeEnvelope("spBv1.0/mygroup/NDATA/eon1", dataPayload));

        events.Should().HaveCount(1);
        events[0].Should().BeOfType<NodeDataEvent>().Subject.Metrics.Should().BeEmpty();
    }

    [Test]
    public void Extract_NBirth_ClearsNodeAliasCache()
    {
        var extractor = new SparkplugTopologyExtractor();

        // First NBIRTH: alias 10 -> "Temperature"
        var birth1 = MakePayloadWithMetrics(
            new Payload.Types.Metric { Name = "Temperature", Alias = 10, Datatype = 3, IntValue = 42 });
        extractor.Extract(MakeEnvelope("spBv1.0/mygroup/NBIRTH/eon1", birth1));

        // Second NBIRTH: alias 10 -> "Humidity" (rebirth changes the mapping)
        var birth2 = MakePayloadWithMetrics(
            new Payload.Types.Metric { Name = "Humidity", Alias = 10, Datatype = 3, IntValue = 55 });
        extractor.Extract(MakeEnvelope("spBv1.0/mygroup/NBIRTH/eon1", birth2));

        // NDATA with alias 10 should resolve to "Humidity"
        var dataPayload = MakePayloadWithMetrics(
            new Payload.Types.Metric { Alias = 10, Datatype = 3, IntValue = 60 });
        var events = extractor.Extract(MakeEnvelope("spBv1.0/mygroup/NDATA/eon1", dataPayload));

        events[0].Should().BeOfType<NodeDataEvent>().Subject.Metrics[0].Name.Should().Be("Humidity");
    }

    [Test]
    public void Extract_MultipleMetrics_AllExtracted()
    {
        var extractor = new SparkplugTopologyExtractor();
        var payload = MakePayloadWithMetrics(
            MakeIntMetric("a", 1),
            MakeStringMetric("b", "two"),
            MakeBoolMetric("c", true));
        var envelope = MakeEnvelope("spBv1.0/g/NDATA/n", payload);

        var metrics = extractor.Extract(envelope)[0].Should().BeOfType<NodeDataEvent>()
            .Subject.Metrics;
        metrics.Should().HaveCount(3);
        metrics[0].Name.Should().Be("a");
        metrics[1].Name.Should().Be("b");
        metrics[2].Name.Should().Be("c");
    }
}
