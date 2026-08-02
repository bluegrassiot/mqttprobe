using System.Text;
using MqttProbe.Models.Emulation;
using MqttProbe.Services.Plugins.Contracts;

namespace MqttProbe.Services.Emulation;

public sealed class HexPayloadEncoder : IPayloadEncoder
{
    public string FormatId => "hex";

    public byte[] Encode(PayloadEncoderRequest request)
    {
        if (request.Metrics.Count == 0)
        {
            throw new ArgumentException(
                "Hex encoding requires at least one metric.",
                nameof(request));
        }

        var (name, raw) = request.Metrics.First();
        var (valueType, value) = JsonPayloadEncoder.ConvertValue(raw);
        var metric = new EmulatorMetricConfig { Name = name, ValueType = valueType };
        var hex = GenericPayloadFormatter.FormatHex(metric, value);
        return Encoding.UTF8.GetBytes(hex);
    }
}
