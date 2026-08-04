using System.Buffers.Text;
using MQTTnet;
using MqttProbe.Services.Plugins.Contracts;

namespace MqttProbe.Services.Plugins.BuiltIn;

public sealed class Base64PayloadDetector : IPayloadDetector
{
    // Short base64 strings (e.g. "test", "null", "true") are common plaintext tokens.
    // 16 bytes is long enough to require at least a few real encoded bytes.
    private const int MinLength = 16;

    public string FormatId => "base64";
    public int Priority => 300;

    public bool CanDetect(MqttApplicationMessageReceivedEventArgs e)
    {
        var segment = e.ApplicationMessage.GetPayloadSegment();
        if (segment.Count == 0)
            return false;

        var bytes = segment.Array.AsSpan(segment.Offset, segment.Count);
        return LooksLikeBase64(bytes);
    }

    private static bool LooksLikeBase64(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < MinLength || bytes.Length % 4 != 0)
            return false;

        return Base64.IsValid(bytes);
    }
}
