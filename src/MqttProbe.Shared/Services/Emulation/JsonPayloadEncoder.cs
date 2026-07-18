using System.Text;
using MqttProbe.Models.Emulation;
using MqttProbe.Services.Plugins.Contracts;

namespace MqttProbe.Services.Emulation;

public sealed class JsonPayloadEncoder : IPayloadEncoder
{
    public string FormatId => "json";

    public byte[] Encode(PayloadEncoderRequest request)
    {
        var timestamp = request.TimestampUtc ?? DateTime.UtcNow;
        var values = ConvertMetrics(request.Metrics);
        var json = GenericPayloadFormatter.FormatDeviceJson(timestamp, values);
        return Encoding.UTF8.GetBytes(json);
    }

    internal static IReadOnlyList<(EmulatorMetricConfig Metric, double Value)> ConvertMetrics(
        IReadOnlyDictionary<string, object> metrics)
    {
        var result = new List<(EmulatorMetricConfig, double)>(metrics.Count);

        foreach (var (name, raw) in metrics)
        {
            var (valueType, value) = ConvertValue(raw);
            result.Add((new EmulatorMetricConfig { Name = name, ValueType = valueType }, value));
        }

        return result;
    }

    internal static (MetricValueType ValueType, double Value) ConvertValue(object raw) =>
        raw switch
        {
            bool b => (MetricValueType.Boolean, b ? 1.0 : 0.0),
            long l => (MetricValueType.Int64, (double)l),
            int i => (MetricValueType.Int64, (double)i),
            double d => (MetricValueType.Double, d),
            float f => (MetricValueType.Double, (double)f),
            _ => (MetricValueType.Double, Convert.ToDouble(raw))
        };
}
