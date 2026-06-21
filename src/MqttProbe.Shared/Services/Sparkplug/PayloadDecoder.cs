using System.Text;
using MessagePack;
using MQTTnet;
using MQTTnet.Client;
using MqttProbe.Services.Mqtt;
using Org.Eclipse.Tahu.Protobuf;

namespace MqttProbe.Services.Sparkplug;

public static class PayloadDecoder
{
    internal static DecodedPayload Decode(MqttApplicationMessageReceivedEventArgs e)
    {
        var segment = e.ApplicationMessage.PayloadSegment;
        if (segment.Count == 0)
            return new DecodedPayload(string.Empty, DetectedPayloadFormat.Empty);

        var format = PayloadFormatDetector.Detect(e);

        var payload = format switch
        {
            DetectedPayloadFormat.Sparkplug => DecodeSparkplug(segment),
            DetectedPayloadFormat.Binary => DecodeBinary(segment),
            DetectedPayloadFormat.MsgPack => DecodeMessagePack(segment),
            _ => e.ApplicationMessage.ConvertPayloadToString() ?? string.Empty
        };

        return new DecodedPayload(payload, format);
    }

    public static string GetPayloadStr(MqttApplicationMessageReceivedEventArgs e) =>
        Decode(e).Payload;

    private static string DecodeSparkplug(ArraySegment<byte> segment)
    {
        try
        {
            return Payload.Parser.ParseFrom(segment.ToArray()).ToString();
        }
        catch
        {
            return Encoding.UTF8.GetString(segment);
        }
    }

    private static string DecodeBinary(ArraySegment<byte> segment)
    {
        var span = segment.Array.AsSpan(segment.Offset, segment.Count);
        var sb = new StringBuilder(span.Length * 2);
        foreach (var b in span)
            sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    private static string DecodeMessagePack(ArraySegment<byte> segment)
    {
        try
        {
            return MessagePackSerializer.ConvertToJson(segment.ToArray());
        }
        catch (MessagePackSerializationException)
        {
            return DecodeBinary(segment);
        }
    }
}

internal sealed record DecodedPayload(string Payload, DetectedPayloadFormat Format);
