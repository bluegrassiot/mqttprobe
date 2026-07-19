using System.Text.Unicode;
using MQTTnet;
using MqttProbe.Services.Plugins.Contracts;

namespace MqttProbe.Services.Plugins.BuiltIn;

public sealed class Base64PayloadDetector : IPayloadDetector
{
    public string FormatId => "base64";
    public int Priority => 300;

    public bool CanDetect(MqttApplicationMessageReceivedEventArgs e)
    {
        var segment = e.ApplicationMessage.GetPayloadSegment();
        if (segment.Count == 0)
            return false;

        var bytes = segment.Array.AsSpan(segment.Offset, segment.Count);
        if (!Utf8.IsValid(bytes))
            return false;

        return LooksLikeBase64(bytes);
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
}
