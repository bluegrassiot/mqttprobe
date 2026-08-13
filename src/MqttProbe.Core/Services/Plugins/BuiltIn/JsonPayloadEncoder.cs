using System.Globalization;
using System.Text;
using MqttProbe.Core.Models.Emulation;
using MqttProbe.PluginContracts;

namespace MqttProbe.Core.Services.Plugins.BuiltIn;

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
            long l => (MetricValueType.Int64, l),
            int i => (MetricValueType.Int64, i),
            double d => (MetricValueType.Double, d),
            float f => (MetricValueType.Double, f),
            // Invariant, not current culture: a string like "1.5" parses to 15 under a
            // comma-decimal locale, silently corrupting the outbound payload.
            _ => (MetricValueType.Double, Convert.ToDouble(raw, CultureInfo.InvariantCulture))
        };
}
