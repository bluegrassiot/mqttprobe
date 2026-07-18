using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MqttProbe.Models.Emulation;
using MqttProbe.Services.Emulation;
using MqttProbe.Services.Plugins.BuiltIn;
using MqttProbe.Services.Plugins.Contracts;
using MqttProbe.Services.Plugins.Pipeline;
using MqttProbe.Services.Plugins.Registry;

namespace MqttProbe.Tests.Services.Plugins.Integration;

[TestFixture]
public class WritePathIntegrationTests
{
    private static PluginRegistry _registry = null!;
    private static PayloadPipeline _pipeline = null!;

    [OneTimeSetUp]
    public void SetUp()
    {
        var builder = new PluginRegistryBuilder();
        BuiltInPluginRegistration.RegisterBuiltIns(builder);
        _registry = builder.Build();
        _pipeline = new PayloadPipeline(_registry, Substitute.For<ILogger<PayloadPipeline>>());
    }

    // --- JSON encoder: round-trip parseable ---

    [Test]
    public void JsonEncoder_Metrics_RoundTripsViaSystemTextJson()
    {
        var timestamp = new DateTime(2025, 6, 30, 12, 0, 0, DateTimeKind.Utc);
        var request = new PayloadEncoderRequest
        {
            Topic = "sensor/data",
            FormatId = "json",
            Metrics = new Dictionary<string, object>
            {
                ["temperature"] = 22.5,
                ["count"] = 42L,
                ["active"] = true
            },
            TimestampUtc = timestamp
        };

        var bytes = _pipeline.EncodeOutbound(request);
        var json = Encoding.UTF8.GetString(bytes);

        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("timestamp").GetString().Should().Be("2025-06-30T12:00:00.000Z");

        var metrics = root.GetProperty("metrics");
        metrics.GetProperty("temperature").GetDouble().Should().Be(22.5);
        metrics.GetProperty("count").GetInt64().Should().Be(42);
        metrics.GetProperty("active").GetBoolean().Should().BeTrue();
    }

    [Test]
    public void JsonEncoder_EmptyMetrics_ProducesValidJsonObject()
    {
        var request = new PayloadEncoderRequest
        {
            Topic = "sensor/data",
            FormatId = "json",
            Metrics = new Dictionary<string, object>(),
            TimestampUtc = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };

        var bytes = _pipeline.EncodeOutbound(request);
        var json = Encoding.UTF8.GetString(bytes);

        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("metrics").EnumerateObject().Should().BeEmpty();
    }

    // --- PlainText encoder: single metric ---

    [Test]
    public void PlainTextEncoder_SingleDoubleMetric_ReturnsExpectedText()
    {
        var request = new PayloadEncoderRequest
        {
            Topic = "sensor/temp",
            FormatId = "plaintext",
            Metrics = new Dictionary<string, object> { ["temperature"] = 22.5 }
        };

        var bytes = _pipeline.EncodeOutbound(request);
        var text = Encoding.UTF8.GetString(bytes);

        text.Should().Be("22.5");
    }

    [Test]
    public void PlainTextEncoder_SingleBooleanMetric_ReturnsExpectedText()
    {
        var request = new PayloadEncoderRequest
        {
            Topic = "sensor/state",
            FormatId = "plaintext",
            Metrics = new Dictionary<string, object> { ["enabled"] = true }
        };

        var bytes = _pipeline.EncodeOutbound(request);
        var text = Encoding.UTF8.GetString(bytes);

        text.Should().Be("true");
    }

    // --- Hex encoder: single metric ---

    [Test]
    public void HexEncoder_SingleDoubleMetric_ReturnsExpectedHex()
    {
        var request = new PayloadEncoderRequest
        {
            Topic = "sensor/temp",
            FormatId = "hex",
            Metrics = new Dictionary<string, object> { ["temperature"] = 22.5 }
        };

        var bytes = _pipeline.EncodeOutbound(request);
        var hex = Encoding.UTF8.GetString(bytes);

        hex.Should().Be("4036800000000000");
    }

    [Test]
    public void HexEncoder_SingleBooleanTrue_ReturnsExpectedHex()
    {
        var request = new PayloadEncoderRequest
        {
            Topic = "sensor/state",
            FormatId = "hex",
            Metrics = new Dictionary<string, object> { ["enabled"] = true }
        };

        var bytes = _pipeline.EncodeOutbound(request);
        var hex = Encoding.UTF8.GetString(bytes);

        hex.Should().Be("01");
    }

    // --- Encoder not found for unknown FormatId ---

    [Test]
    public void EncodeOutbound_UnknownFormatId_ThrowsInvalidOperationException()
    {
        var request = new PayloadEncoderRequest
        {
            Topic = "sensor/data",
            FormatId = "unknown-format",
            Metrics = new Dictionary<string, object> { ["x"] = 1.0 }
        };

        var act = () => _pipeline.EncodeOutbound(request);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*unknown-format*");
    }

    // --- Spot-check parity with GenericPayloadFormatter ---

    [Test]
    public void JsonEncoder_SpotCheck_MatchesGenericPayloadFormatter()
    {
        var timestamp = new DateTime(2025, 6, 30, 12, 0, 0, DateTimeKind.Utc);
        var request = new PayloadEncoderRequest
        {
            Topic = "sensor/data",
            FormatId = "json",
            Metrics = new Dictionary<string, object> { ["temperature"] = 22.5 },
            TimestampUtc = timestamp
        };

        var bytes = _pipeline.EncodeOutbound(request);
        var actual = Encoding.UTF8.GetString(bytes);

        var expected = GenericPayloadFormatter.FormatDeviceJson(
            timestamp,
            [(new EmulatorMetricConfig { Name = "temperature", ValueType = MetricValueType.Double }, 22.5)]);

        actual.Should().Be(expected);
    }

    [Test]
    public void PlainTextEncoder_SpotCheck_MatchesGenericPayloadFormatter()
    {
        var request = new PayloadEncoderRequest
        {
            Topic = "sensor/temp",
            FormatId = "plaintext",
            Metrics = new Dictionary<string, object> { ["temperature"] = 22.5 }
        };

        var bytes = _pipeline.EncodeOutbound(request);
        var actual = Encoding.UTF8.GetString(bytes);

        var expected = GenericPayloadFormatter.FormatPlainText(
            new EmulatorMetricConfig { Name = "temperature", ValueType = MetricValueType.Double }, 22.5);

        actual.Should().Be(expected);
    }

    [Test]
    public void HexEncoder_SpotCheck_MatchesGenericPayloadFormatter()
    {
        var request = new PayloadEncoderRequest
        {
            Topic = "sensor/temp",
            FormatId = "hex",
            Metrics = new Dictionary<string, object> { ["temperature"] = 22.5 }
        };

        var bytes = _pipeline.EncodeOutbound(request);
        var actual = Encoding.UTF8.GetString(bytes);

        var expected = GenericPayloadFormatter.FormatHex(
            new EmulatorMetricConfig { Name = "temperature", ValueType = MetricValueType.Double }, 22.5);

        actual.Should().Be(expected);
    }
}
