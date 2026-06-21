using System.Text.Json;
using MqttProbe.Models.Emulation;
using MqttProbe.Services.Emulation;

namespace MqttProbe.Shared.Tests.Services.Emulation;

[TestFixture]
public class GenericPayloadFormatterTests
{
    private static EmulatorMetricConfig Metric(string name, MetricValueType valueType) =>
        new() { Name = name, ValueType = valueType };

    [Test]
    public void FormatDeviceJson_ProducesIso8601UtcTimestampAndTypedMetrics()
    {
        var timestamp = new DateTime(2026, 6, 10, 14, 3, 5, 123, DateTimeKind.Utc);
        var values = new List<(EmulatorMetricConfig Metric, double Value)>
        {
            (Metric("Flow Rate", MetricValueType.Double), 22.14),
            (Metric("Valve Open", MetricValueType.Boolean), 1.0),
            (Metric("Counter", MetricValueType.Int64), 42.0)
        };

        var json = GenericPayloadFormatter.FormatDeviceJson(timestamp, values);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("timestamp").GetString().Should().Be("2026-06-10T14:03:05.123Z");
        var metrics = doc.RootElement.GetProperty("metrics");
        metrics.GetProperty("Flow Rate").ValueKind.Should().Be(JsonValueKind.Number);
        metrics.GetProperty("Flow Rate").GetDouble().Should().Be(22.14);
        metrics.GetProperty("Valve Open").ValueKind.Should().Be(JsonValueKind.True);
        metrics.GetProperty("Counter").ValueKind.Should().Be(JsonValueKind.Number);
        metrics.GetProperty("Counter").GetInt64().Should().Be(42);
        metrics.GetProperty("Counter").GetRawText().Should().Be("42");
    }

    [Test]
    public void FormatDeviceJson_BooleanZero_SerializesFalse()
    {
        var values = new List<(EmulatorMetricConfig Metric, double Value)>
        {
            (Metric("Valve Open", MetricValueType.Boolean), 0.0)
        };

        var json = GenericPayloadFormatter.FormatDeviceJson(DateTime.UnixEpoch, values);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("metrics").GetProperty("Valve Open").ValueKind
            .Should().Be(JsonValueKind.False);
    }

    [Test]
    public void FormatPlainText_Double_UsesInvariantCulture()
    {
        var value = GenericPayloadFormatter.FormatPlainText(Metric("M", MetricValueType.Double), 22.14);

        value.Should().Be("22.14");
    }

    [Test]
    public void FormatPlainText_BooleanTrue_IsLowercaseTrue()
    {
        var value = GenericPayloadFormatter.FormatPlainText(Metric("M", MetricValueType.Boolean), 1.0);

        value.Should().Be("true");
    }

    [Test]
    public void FormatPlainText_BooleanFalse_IsLowercaseFalse()
    {
        var value = GenericPayloadFormatter.FormatPlainText(Metric("M", MetricValueType.Boolean), 0.0);

        value.Should().Be("false");
    }

    [Test]
    public void FormatPlainText_Int64_HasNoDecimals()
    {
        var value = GenericPayloadFormatter.FormatPlainText(Metric("M", MetricValueType.Int64), 42.0);

        value.Should().Be("42");
    }

    [Test]
    public void FormatHex_DoubleOne_IsIeee754BigEndian()
    {
        var value = GenericPayloadFormatter.FormatHex(Metric("M", MetricValueType.Double), 1.0);

        value.Should().Be("3FF0000000000000");
    }

    [Test]
    public void FormatHex_DoubleZero_IsAllZeros()
    {
        var value = GenericPayloadFormatter.FormatHex(Metric("M", MetricValueType.Double), 0.0);

        value.Should().Be("0000000000000000");
    }

    [Test]
    public void FormatHex_Int64_IsTwosComplementBigEndian()
    {
        GenericPayloadFormatter.FormatHex(Metric("M", MetricValueType.Int64), 255.0)
            .Should().Be("00000000000000FF");
        GenericPayloadFormatter.FormatHex(Metric("M", MetricValueType.Int64), -1.0)
            .Should().Be("FFFFFFFFFFFFFFFF");
    }

    [Test]
    public void FormatHex_Boolean_IsSingleByte()
    {
        GenericPayloadFormatter.FormatHex(Metric("M", MetricValueType.Boolean), 1.0).Should().Be("01");
        GenericPayloadFormatter.FormatHex(Metric("M", MetricValueType.Boolean), 0.0).Should().Be("00");
    }
}
