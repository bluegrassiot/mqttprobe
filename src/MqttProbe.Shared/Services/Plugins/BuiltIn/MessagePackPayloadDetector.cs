using System.Buffers;
using MessagePack;
using MQTTnet.Client;
using MqttProbe.Services.Plugins.Contracts;

namespace MqttProbe.Services.Plugins.BuiltIn;

public sealed class MessagePackPayloadDetector : IPayloadDetector
{
    public string FormatId => "messagepack";
    public int Priority => 800;

    public bool CanDetect(MqttApplicationMessageReceivedEventArgs e)
    {
        var segment = e.ApplicationMessage.PayloadSegment;
        if (segment.Count == 0)
            return false;

        var bytes = segment.Array.AsSpan(segment.Offset, segment.Count);
        return LooksLikeMessagePack(bytes);
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
}
