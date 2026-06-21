using System.Globalization;
using System.Text;
using System.Text.Json;
using MqttProbe.Models.Emulation;

namespace MqttProbe.Services.Emulation;

public static class GenericPayloadFormatter
{
    public static string FormatDeviceJson(
        DateTime timestampUtc,
        IReadOnlyList<(EmulatorMetricConfig Metric, double Value)> values)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("timestamp",
                timestampUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
            writer.WriteStartObject("metrics");
            foreach (var (metric, value) in values)
            {
                switch (metric.ValueType)
                {
                    case MetricValueType.Boolean:
                        writer.WriteBoolean(metric.Name, value >= 0.5);
                        break;
                    case MetricValueType.Int64:
                        writer.WriteNumber(metric.Name, (long)Math.Round(value));
                        break;
                    default:
                        writer.WriteNumber(metric.Name, value);
                        break;
                }
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string FormatPlainText(EmulatorMetricConfig metric, double value) =>
        metric.ValueType switch
        {
            MetricValueType.Boolean => value >= 0.5 ? "true" : "false",
            MetricValueType.Int64 => ((long)Math.Round(value)).ToString(CultureInfo.InvariantCulture),
            _ => value.ToString(CultureInfo.InvariantCulture)
        };

    public static string FormatHex(EmulatorMetricConfig metric, double value) =>
        // Uppercase big-endian byte text: IEEE 754 for doubles, two's complement for Int64, 01/00 for booleans.
        metric.ValueType switch
        {
            MetricValueType.Boolean => value >= 0.5 ? "01" : "00",
            MetricValueType.Int64 => ((long)Math.Round(value)).ToString("X16", CultureInfo.InvariantCulture),
            _ => BitConverter.DoubleToInt64Bits(value).ToString("X16", CultureInfo.InvariantCulture)
        };
}
