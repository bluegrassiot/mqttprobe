using System.Text;
using MQTTnet.Client;
using MqttProbe.Services.Plugins.Contracts;

namespace MqttProbe.Services.Plugins.BuiltIn;

public sealed class HexPayloadDecoder : IPayloadDecoder
{
    public string FormatId => "hex";

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
            var hexText = Encoding.UTF8.GetString(raw);
            var decodedBytes = Convert.FromHexString(hexText);

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
                $"Invalid hex: {ex.Message}");
        }
    }
}
