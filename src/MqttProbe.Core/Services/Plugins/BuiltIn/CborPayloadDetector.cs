using System.Text.Unicode;
using MQTTnet;
using MqttProbe.PluginContracts;

namespace MqttProbe.Core.Services.Plugins.BuiltIn;

public sealed class CborPayloadDetector : IPayloadDetector
{
    public string FormatId => "cbor";
    public int Priority => 750;
    public string DisplayName => "CBOR";

    public bool CanDetect(MqttApplicationMessageReceivedEventArgs e)
    {
        var segment = e.ApplicationMessage.GetPayloadSegment();
        if (segment.Count == 0)
            return false;

        return LooksLikeCbor(segment.Array.AsSpan(segment.Offset, segment.Count));
    }

    private static bool LooksLikeCbor(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || !CanStartCbor(bytes[0]))
            return false;

        if (!CborStructureValidator.TryValidate(bytes))
            return false;

        if (bytes[0] >> 5 == 2 && IsEmptyByteString(bytes))
            return false;

        return !IsPrintableUtf8(bytes);
    }

    private static bool CanStartCbor(byte b) => b >> 5 != 3;

    private static bool IsEmptyByteString(ReadOnlySpan<byte> bytes) =>
        bytes[0] == 0x5F
            ? bytes.Length == 2 && bytes[1] == 0xFF
            : bytes[0] == 0x40 || (bytes[0] == 0x58 && bytes.Length > 1 && bytes[1] == 0);

    private static bool IsPrintableUtf8(ReadOnlySpan<byte> bytes)
    {
        if (!Utf8.IsValid(bytes))
            return false;

        foreach (var b in bytes)
        {
            if (b < 0x20 || b == 0x7F)
                return false;
        }

        return true;
    }
}
