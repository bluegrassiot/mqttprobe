using System.Text;
using MqttProbe.Core.Models.Emulation;
using MqttProbe.PluginContracts;

namespace MqttProbe.Core.Services.Plugins.BuiltIn;

public sealed class PlainTextPayloadEncoder : IPayloadEncoder
{
    public string FormatId => "plaintext";

    public byte[] Encode(PayloadEncoderRequest request)
    {
        if (request.Metrics.Count == 0)
        {
            throw new ArgumentException(
                "PlainText encoding requires at least one metric.",
                nameof(request));
        }

        var (name, raw) = request.Metrics.First();
        var (valueType, value) = JsonPayloadEncoder.ConvertValue(raw);
        var metric = new EmulatorMetricConfig { Name = name, ValueType = valueType };
        var text = GenericPayloadFormatter.FormatPlainText(metric, value);
        return Encoding.UTF8.GetBytes(text);
    }
}
