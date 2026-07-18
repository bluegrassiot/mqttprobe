using System.Text.Unicode;
using MQTTnet.Client;
using MqttProbe.Services.Plugins.Contracts;

namespace MqttProbe.Services.Plugins.BuiltIn;

public sealed class HexPayloadDetector : IPayloadDetector
{
    public string FormatId => "hex";
    public int Priority => 400;

    public bool CanDetect(MqttApplicationMessageReceivedEventArgs e)
    {
        var segment = e.ApplicationMessage.PayloadSegment;
        if (segment.Count == 0)
            return false;

        var bytes = segment.Array.AsSpan(segment.Offset, segment.Count);
        if (!Utf8.IsValid(bytes))
            return false;

        return LooksLikeHex(bytes);
    }

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
