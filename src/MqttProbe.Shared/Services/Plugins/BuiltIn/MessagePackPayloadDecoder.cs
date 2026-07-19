using System.Text;
using MessagePack;
using MQTTnet;
using MqttProbe.Services.Plugins.Contracts;

namespace MqttProbe.Services.Plugins.BuiltIn;

public sealed class MessagePackPayloadDecoder : IPayloadDecoder
{
    public string FormatId => "messagepack";

    public DecodedPayloadEnvelope Decode(MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;
        var segment = e.ApplicationMessage.GetPayloadSegment();
        var raw = segment.Array is null ? [] : segment.ToArray();

        if (raw.Length == 0)
        {
            return DecodedPayloadEnvelope.CreateSuccess(
                FormatId, topic, raw, string.Empty);
        }

        try
        {
            var json = MessagePackSerializer.ConvertToJson(raw);
            return DecodedPayloadEnvelope.CreateSuccess(
                FormatId, topic, raw, json);
        }
        catch (MessagePackSerializationException)
        {
            // On deserialization failure the old decoder fell back to a hex
            // dump. We preserve that behavior here. This is a success envelope
            // because the detector already matched the payload as MessagePack
            // and we want the UI to show the hex bytes rather than an error.
            return DecodedPayloadEnvelope.CreateSuccess(
                FormatId, topic, raw, HexDump(raw));
        }
    }

    internal static string HexDump(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
            sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
