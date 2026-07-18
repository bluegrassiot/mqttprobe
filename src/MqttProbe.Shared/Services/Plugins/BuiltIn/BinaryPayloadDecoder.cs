using System.Text;
using MQTTnet.Client;
using MqttProbe.Services.Plugins.Contracts;

namespace MqttProbe.Services.Plugins.BuiltIn;

public sealed class BinaryPayloadDecoder : IPayloadDecoder
{
    public string FormatId => "binary";

    public DecodedPayloadEnvelope Decode(MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;
        var segment = e.ApplicationMessage.PayloadSegment;
        var raw = segment.Array is null ? [] : segment.ToArray();

        return DecodedPayloadEnvelope.CreateSuccess(
            FormatId, topic, raw, HexDump(raw));
    }

    internal static string HexDump(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
            sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    internal static bool IsValidUtf8(byte[] bytes)
    {
        var decoder = Encoding.UTF8.GetDecoder();
        decoder.Fallback = DecoderFallback.ExceptionFallback;
        try
        {
            decoder.GetCharCount(bytes, true);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
