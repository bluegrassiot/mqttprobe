using System.Text;
using Google.Protobuf;
using MessagePack;
using MqttProbe.Benchmarks.Models;
using Org.Eclipse.Tahu.Protobuf;

namespace MqttProbe.Benchmarks;

public static class PayloadFactory
{
    private const string SampleJson = """{"temperature":22.5,"pressure":1013.25,"flowRate":12.8,"status":"active"}""";
    private const ulong SparkplugTimestamp = 1700000000000;

    private const uint DataTypeDouble = 10;
    private const uint DataTypeString = 12;

    // Pre-computed helpers
    private static readonly byte[] _jsonBytes = Encoding.UTF8.GetBytes(SampleJson);
    private static readonly byte[] _hexBytes = Encoding.UTF8.GetBytes(ConvertToHex(_jsonBytes));
    private static readonly byte[] _base64Bytes = Encoding.UTF8.GetBytes(Convert.ToBase64String(_jsonBytes));

    public static byte[] CreateSample(PayloadFormat format) => format switch
    {
        PayloadFormat.Empty => [],
        PayloadFormat.Sparkplug => CreateSparkplugPayload().ToByteArray(),
        PayloadFormat.MessagePack => MessagePackSerializer.Serialize(CreateMsgPackData()),
        PayloadFormat.Binary => [0xFF, 0xFE, 0x00, 0x01, 0x80, 0xC0],
        PayloadFormat.Json => _jsonBytes,
        PayloadFormat.Xml => "<sensor><temp>22.5</temp></sensor>"u8.ToArray(),
        PayloadFormat.Hex => _hexBytes,
        PayloadFormat.Base64 => _base64Bytes,
        PayloadFormat.PlainText => "hello from mqttprobe temperature=22.5"u8.ToArray(),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
    };

    // ── Hex ──────────────────────────────────────────────────────────────────

    private static string ConvertToHex(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return string.Empty;
        var sb = new StringBuilder(bytes.Length * 2);
        for (var i = 0; i < bytes.Length; i++)
            sb.Append(bytes[i].ToString("x2"));
        return sb.ToString();
    }

    // ── MessagePack ──────────────────────────────────────────────────────────

    [MessagePackObject]
    public class SensorData
    {
        [Key(0)] public double Temperature { get; set; }
        [Key(1)] public double Pressure { get; set; }
        [Key(2)] public double FlowRate { get; set; }
        [Key(3)] public string Status { get; set; } = "";
    }

    private static SensorData CreateMsgPackData() => new()
    {
        Temperature = 22.5,
        Pressure = 1013.25,
        FlowRate = 12.8,
        Status = "active"
    };

    // ── Sparkplug B ──────────────────────────────────────────────────────────

    private static Payload CreateSparkplugPayload()
    {
        var payload = new Payload { Timestamp = SparkplugTimestamp, Seq = 42 };
        payload.Metrics.Add(CreateMetric("Temperature", DataTypeDouble, 22.5));
        payload.Metrics.Add(CreateMetric("Pressure", DataTypeDouble, 1013.25));
        payload.Metrics.Add(CreateMetric("Flow Rate", DataTypeDouble, 12.8));
        payload.Metrics.Add(CreateMetric("Status", DataTypeString, "active"));
        return payload;
    }

    private static Payload.Types.Metric CreateMetric(string name, uint dataType, object value)
    {
        var metric = new Payload.Types.Metric { Name = name, Timestamp = SparkplugTimestamp, Datatype = dataType };
        switch (value)
        {
            case double d: metric.DoubleValue = d; break;
            case string s: metric.StringValue = s; break;
        }
        return metric;
    }
}
