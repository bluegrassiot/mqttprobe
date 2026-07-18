using System.Text;
using MQTTnet.Client;
using MqttProbe.Services.Plugins.Contracts;

namespace MqttProbe.Services.Plugins.BuiltIn;

public sealed class Base64PayloadDecoder : IPayloadDecoder
{
    public string FormatId => "base64";

    public DecodedPayloadEnvelope Decode(MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;
        var segment = e.ApplicationMessage.PayloadSegment;
        var raw = segment.Array is null ? [] : segment.ToArray();

        if (raw.Length == 0)
        {
            return DecodedPayloadEnvelope.CreateSuccess(
                FormatId, topic, raw, string.Empty);
        }

        try
        {
            var encoded = Encoding.UTF8.GetString(raw);
            var decodedBytes = Convert.FromBase64String(encoded);

            var displayText = BinaryPayloadDecoder.IsValidUtf8(decodedBytes)
                ? Encoding.UTF8.GetString(decodedBytes)
                : BinaryPayloadDecoder.HexDump(decodedBytes);

            return DecodedPayloadEnvelope.CreateSuccess(
                FormatId, topic, raw, displayText);
        }
        catch (FormatException ex)
        {
            return DecodedPayloadEnvelope.CreateFailure(
                FormatId, topic, raw,
                $"Invalid base64: {ex.Message}");
        }
    }
}
