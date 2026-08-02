using System.Text;
using MQTTnet;
using MqttProbe.Services.Plugins.Contracts;

namespace MqttProbe.Services.Plugins.BuiltIn;

public sealed class BinaryPayloadDecoder : IPayloadDecoder
{
    public string FormatId => "binary";

    public DecodedPayloadEnvelope Decode(MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;
        var segment = e.ApplicationMessage.GetPayloadSegment();
        var raw = segment.Array is null ? [] : segment.ToArray();

        return DecodedPayloadEnvelope.CreateSuccess(
            FormatId, topic, raw, HexDump(raw));
    }

    internal static string HexDump(byte[] bytes) =>
        Convert.ToHexString(bytes).ToLowerInvariant();

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
