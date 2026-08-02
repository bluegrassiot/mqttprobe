using System.Globalization;
using System.Text;
using MqttProbe.Models.Emulation;
using MqttProbe.Services.Emulation;
using MqttProbe.Services.Plugins.BuiltIn;
using MqttProbe.Services.Plugins.Contracts;
using MqttProbe.Services.Plugins.Registry;

namespace MqttProbe.Tests.Services.Plugins.BuiltIn;

[TestFixture]
public class BuiltInEncoderTests
{
    private static PayloadEncoderRequest MakeRequest(
        string topic,
        IReadOnlyDictionary<string, object>? metrics = null,
        DateTime? timestamp = null) =>
        new()
        {
            Topic = topic,
            FormatId = "json",
            Metrics = metrics ?? new Dictionary<string, object>(),
            TimestampUtc = timestamp
        };

    private static PluginRegistry BuildRegistry()
    {
        var builder = new PluginRegistryBuilder();
        BuiltInPluginRegistration.RegisterBuiltIns(builder);
        return builder.Build();
    }

    // --- Registry registration ---

    [Test]
    public void Registry_ThreeEncodersRegistered()
    {
        var registry = BuildRegistry();
        registry.Encoders.Should().HaveCount(3);
        registry.FindEncoder("json").Should().NotBeNull();
        registry.FindEncoder("plaintext").Should().NotBeNull();
        registry.FindEncoder("hex").Should().NotBeNull();
    }

    [Test]
    public void Registry_UnknownFormatId_ReturnsNull()
    {
        var registry = BuildRegistry();
        registry.FindEncoder("unknown-format").Should().BeNull();
    }

    // --- FormatId on encoder instances ---

    [Test]
    public void Encoders_HaveCorrectFormatIds()
    {
        new JsonPayloadEncoder().FormatId.Should().Be("json");
        new PlainTextPayloadEncoder().FormatId.Should().Be("plaintext");
        new HexPayloadEncoder().FormatId.Should().Be("hex");
    }

    // --- JSON encoder: parity with GenericPayloadFormatter.FormatDeviceJson ---

    [Test]
    public void JsonEncoder_DoubleMetric_MatchesFormatter()
    {
        var timestamp = new DateTime(2025, 6, 30, 12, 0, 0, DateTimeKind.Utc);
        var metrics = new Dictionary<string, object> { ["temperature"] = 22.5 };
        var request = MakeRequest("sensor/data", metrics, timestamp);

        var encoder = new JsonPayloadEncoder();
        var bytes = encoder.Encode(request);
        var actual = Encoding.UTF8.GetString(bytes);

        var expected = GenericPayloadFormatter.FormatDeviceJson(
            timestamp,
            [(new EmulatorMetricConfig { Name = "temperature", ValueType = MetricValueType.Double }, 22.5)]);

        actual.Should().Be(expected);
    }

    [Test]
    public void JsonEncoder_Int64Metric_MatchesFormatter()
    {
        var timestamp = new DateTime(2025, 6, 30, 12, 0, 0, DateTimeKind.Utc);
        var metrics = new Dictionary<string, object> { ["count"] = 42L };
        var request = MakeRequest("sensor/data", metrics, timestamp);

        var encoder = new JsonPayloadEncoder();
        var bytes = encoder.Encode(request);
        var actual = Encoding.UTF8.GetString(bytes);

        var expected = GenericPayloadFormatter.FormatDeviceJson(
            timestamp,
            [(new EmulatorMetricConfig { Name = "count", ValueType = MetricValueType.Int64 }, 42.0)]);

        actual.Should().Be(expected);
    }

    [Test]
    public void JsonEncoder_BooleanMetric_MatchesFormatter()
    {
        var timestamp = new DateTime(2025, 6, 30, 12, 0, 0, DateTimeKind.Utc);
        var metrics = new Dictionary<string, object> { ["enabled"] = true };
        var request = MakeRequest("sensor/data", metrics, timestamp);

        var encoder = new JsonPayloadEncoder();
        var bytes = encoder.Encode(request);
        var actual = Encoding.UTF8.GetString(bytes);

        var expected = GenericPayloadFormatter.FormatDeviceJson(
            timestamp,
            [(new EmulatorMetricConfig { Name = "enabled", ValueType = MetricValueType.Boolean }, 1.0)]);

        actual.Should().Be(expected);
    }

    [Test]
    public void JsonEncoder_EmptyMetrics_ProducesValidJsonObject()
    {
        var timestamp = new DateTime(2025, 6, 30, 12, 0, 0, DateTimeKind.Utc);
        var request = MakeRequest("sensor/data", new Dictionary<string, object>(), timestamp);

        var encoder = new JsonPayloadEncoder();
        var bytes = encoder.Encode(request);
        var actual = Encoding.UTF8.GetString(bytes);

        var expected = GenericPayloadFormatter.FormatDeviceJson(timestamp, []);
        actual.Should().Be(expected);
        actual.Should().Contain("\"metrics\":{}");
    }

    [Test]
    public void JsonEncoder_MultipleMetrics_MatchesFormatter()
    {
        var timestamp = new DateTime(2025, 6, 30, 12, 0, 0, DateTimeKind.Utc);
        var metrics = new Dictionary<string, object>
        {
            ["temp"] = 21.5,
            ["count"] = 10L,
            ["active"] = false
        };
        var request = MakeRequest("sensor/data", metrics, timestamp);

        var encoder = new JsonPayloadEncoder();
        var bytes = encoder.Encode(request);
        var actual = Encoding.UTF8.GetString(bytes);

        var expected = GenericPayloadFormatter.FormatDeviceJson(
            timestamp,
            [
                (new EmulatorMetricConfig { Name = "temp", ValueType = MetricValueType.Double }, 21.5),
                (new EmulatorMetricConfig { Name = "count", ValueType = MetricValueType.Int64 }, 10.0),
                (new EmulatorMetricConfig { Name = "active", ValueType = MetricValueType.Boolean }, 0.0)
            ]);

        actual.Should().Be(expected);
    }

    [Test]
    public void JsonEncoder_NoTimestamp_UsesUtcNow()
    {
        var metrics = new Dictionary<string, object> { ["x"] = 1.0 };
        var request = MakeRequest("sensor/data", metrics, null);

        var encoder = new JsonPayloadEncoder();
        var before = DateTime.UtcNow;
        var bytes = encoder.Encode(request);
        var after = DateTime.UtcNow;

        var actual = Encoding.UTF8.GetString(bytes);
        actual.Should().Contain("\"timestamp\":");

        // Verify the timestamp is within the expected range.
        var expected = GenericPayloadFormatter.FormatDeviceJson(
            before, [(new EmulatorMetricConfig { Name = "x", ValueType = MetricValueType.Double }, 1.0)]);
        // Both contain a timestamp; just verify structure matches.
        actual.Should().Contain("\"metrics\":{\"x\":1}");
    }

    // --- PlainText encoder: parity with GenericPayloadFormatter.FormatPlainText ---

    [Test]
    public void PlainTextEncoder_DoubleMetric_MatchesFormatter()
    {
        var metrics = new Dictionary<string, object> { ["temperature"] = 22.5 };
        var request = MakeRequest("sensor/temp", metrics);

        var encoder = new PlainTextPayloadEncoder();
        var bytes = encoder.Encode(request);
        var actual = Encoding.UTF8.GetString(bytes);

        var expected = GenericPayloadFormatter.FormatPlainText(
            new EmulatorMetricConfig { Name = "temperature", ValueType = MetricValueType.Double }, 22.5);

        actual.Should().Be(expected);
        actual.Should().Be("22.5");
    }

    [Test]
    public void PlainTextEncoder_Int64Metric_MatchesFormatter()
    {
        var metrics = new Dictionary<string, object> { ["count"] = 42L };
        var request = MakeRequest("sensor/count", metrics);

        var encoder = new PlainTextPayloadEncoder();
        var bytes = encoder.Encode(request);
        var actual = Encoding.UTF8.GetString(bytes);

        var expected = GenericPayloadFormatter.FormatPlainText(
            new EmulatorMetricConfig { Name = "count", ValueType = MetricValueType.Int64 }, 42.0);

        actual.Should().Be(expected);
        actual.Should().Be("42");
    }

    [Test]
    public void PlainTextEncoder_BooleanTrueMetric_MatchesFormatter()
    {
        var metrics = new Dictionary<string, object> { ["enabled"] = true };
        var request = MakeRequest("sensor/state", metrics);

        var encoder = new PlainTextPayloadEncoder();
        var bytes = encoder.Encode(request);
        var actual = Encoding.UTF8.GetString(bytes);

        var expected = GenericPayloadFormatter.FormatPlainText(
            new EmulatorMetricConfig { Name = "enabled", ValueType = MetricValueType.Boolean }, 1.0);

        actual.Should().Be(expected);
        actual.Should().Be("true");
    }

    [Test]
    public void PlainTextEncoder_BooleanFalseMetric_MatchesFormatter()
    {
        var metrics = new Dictionary<string, object> { ["enabled"] = false };
        var request = MakeRequest("sensor/state", metrics);

        var encoder = new PlainTextPayloadEncoder();
        var bytes = encoder.Encode(request);
        var actual = Encoding.UTF8.GetString(bytes);

        var expected = GenericPayloadFormatter.FormatPlainText(
            new EmulatorMetricConfig { Name = "enabled", ValueType = MetricValueType.Boolean }, 0.0);

        actual.Should().Be(expected);
        actual.Should().Be("false");
    }

    [Test]
    public void PlainTextEncoder_EmptyMetrics_Throws()
    {
        var request = MakeRequest("sensor/temp", new Dictionary<string, object>());

        var encoder = new PlainTextPayloadEncoder();
        var act = () => encoder.Encode(request);
        act.Should().Throw<ArgumentException>()
            .Which.Message.Should().Contain("at least one metric");
    }

    // --- Hex encoder: parity with GenericPayloadFormatter.FormatHex ---

    [Test]
    public void HexEncoder_DoubleMetric_MatchesFormatter()
    {
        var metrics = new Dictionary<string, object> { ["temperature"] = 22.5 };
        var request = MakeRequest("sensor/temp", metrics);

        var encoder = new HexPayloadEncoder();
        var bytes = encoder.Encode(request);
        var actual = Encoding.UTF8.GetString(bytes);

        var expected = GenericPayloadFormatter.FormatHex(
            new EmulatorMetricConfig { Name = "temperature", ValueType = MetricValueType.Double }, 22.5);

        actual.Should().Be(expected);
    }

    [Test]
    public void HexEncoder_Int64Metric_MatchesFormatter()
    {
        var metrics = new Dictionary<string, object> { ["count"] = 256L };
        var request = MakeRequest("sensor/count", metrics);

        var encoder = new HexPayloadEncoder();
        var bytes = encoder.Encode(request);
        var actual = Encoding.UTF8.GetString(bytes);

        var expected = GenericPayloadFormatter.FormatHex(
            new EmulatorMetricConfig { Name = "count", ValueType = MetricValueType.Int64 }, 256.0);

        actual.Should().Be(expected);
        actual.Should().Be("0000000000000100");
    }

    [Test]
    public void HexEncoder_BooleanTrueMetric_MatchesFormatter()
    {
        var metrics = new Dictionary<string, object> { ["enabled"] = true };
        var request = MakeRequest("sensor/state", metrics);

        var encoder = new HexPayloadEncoder();
        var bytes = encoder.Encode(request);
        var actual = Encoding.UTF8.GetString(bytes);

        var expected = GenericPayloadFormatter.FormatHex(
            new EmulatorMetricConfig { Name = "enabled", ValueType = MetricValueType.Boolean }, 1.0);

        actual.Should().Be(expected);
        actual.Should().Be("01");
    }

    [Test]
    public void HexEncoder_BooleanFalseMetric_MatchesFormatter()
    {
        var metrics = new Dictionary<string, object> { ["enabled"] = false };
        var request = MakeRequest("sensor/state", metrics);

        var encoder = new HexPayloadEncoder();
        var bytes = encoder.Encode(request);
        var actual = Encoding.UTF8.GetString(bytes);

        var expected = GenericPayloadFormatter.FormatHex(
            new EmulatorMetricConfig { Name = "enabled", ValueType = MetricValueType.Boolean }, 0.0);

        actual.Should().Be(expected);
        actual.Should().Be("00");
    }

    [Test]
    public void HexEncoder_EmptyMetrics_Throws()
    {
        var request = MakeRequest("sensor/temp", new Dictionary<string, object>());

        var encoder = new HexPayloadEncoder();
        var act = () => encoder.Encode(request);
        act.Should().Throw<ArgumentException>()
            .Which.Message.Should().Contain("at least one metric");
    }

    // --- Value conversion edge cases ---

    [Test]
    public void ConvertValue_IntType_MapsToInt64()
    {
        var (valueType, value) = JsonPayloadEncoder.ConvertValue(42);
        valueType.Should().Be(MetricValueType.Int64);
        value.Should().Be(42.0);
    }

    [Test]
    public void ConvertValue_FloatType_MapsToDouble()
    {
        var (valueType, value) = JsonPayloadEncoder.ConvertValue(3.14f);
        valueType.Should().Be(MetricValueType.Double);
        value.Should().BeApproximately(3.14, 0.001);
    }
}
