using System.Buffers;
using System.Text.Unicode;
using MessagePack;
using MQTTnet.Client;

namespace MqttProbe.Services.Mqtt;

public enum DetectedPayloadFormat
{
    Empty,
    PlainText,
    Json,
    Xml,
    Base64,
    Hex,
    MsgPack,
    Sparkplug,
    Binary
}

public static class PayloadFormatDetector
{
    public static DetectedPayloadFormat Detect(MqttApplicationMessageReceivedEventArgs e)
    {
        var segment = e.ApplicationMessage.PayloadSegment;
        if (segment.Count == 0)
            return DetectedPayloadFormat.Empty;

        var bytes = segment.Array.AsSpan(segment.Offset, segment.Count);

        if (e.ApplicationMessage.Topic.StartsWith("spBv1.0", StringComparison.Ordinal)
            && !Utf8.IsValid(bytes))
        {
            return DetectedPayloadFormat.Sparkplug;
        }

        if (LooksLikeMessagePack(bytes))
            return DetectedPayloadFormat.MsgPack;

        if (!Utf8.IsValid(bytes))
            return DetectedPayloadFormat.Binary;

        var first = bytes[0];
        var last = bytes[^1];

        if ((first == '{' && last == '}') || (first == '[' && last == ']'))
            return DetectedPayloadFormat.Json;

        if (first == '<')
            return DetectedPayloadFormat.Xml;

        if (LooksLikeHex(bytes))
            return DetectedPayloadFormat.Hex;

        if (LooksLikeBase64(bytes))
            return DetectedPayloadFormat.Base64;

        return DetectedPayloadFormat.PlainText;
    }

    private static bool LooksLikeBase64(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4 || bytes.Length % 4 != 0)
            return false;

        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            if (b is (>= (byte)'A' and <= (byte)'Z') or
                (>= (byte)'a' and <= (byte)'z') or
                (>= (byte)'0' and <= (byte)'9') or
                (byte)'+' or (byte)'/' or (byte)'=')
                continue;
            return false;
        }

        var paddingStart = bytes.IndexOf((byte)'=');
        if (paddingStart < 0)
            return true;

        var paddingLen = bytes.Length - paddingStart;
        return paddingLen <= 2 && bytes[paddingStart..].TrimStart((byte)'=').IsEmpty;
    }

    private static bool LooksLikeMessagePack(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || !CanStartStructuredMessagePack(bytes[0]))
            return false;

        try
        {
            var sequence = new ReadOnlySequence<byte>(bytes.ToArray());
            var reader = new MessagePackReader(sequence);
            reader.Skip();
            return reader.End;
        }
        catch (MessagePackSerializationException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool CanStartStructuredMessagePack(byte b) =>
        (b & 0xF0) == 0x80 ||
        (b & 0xF0) == 0x90 ||
        b is 0xDC or 0xDD or 0xDE or 0xDF;

    private static bool LooksLikeHex(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 2 || bytes.Length % 2 != 0)
            return false;

        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            if (b is not ((>= (byte)'0' and <= (byte)'9') or
                          (>= (byte)'a' and <= (byte)'f') or
                          (>= (byte)'A' and <= (byte)'F')))
                return false;
        }

        return true;
    }
}
