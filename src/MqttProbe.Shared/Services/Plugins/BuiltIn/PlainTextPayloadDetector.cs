using System.Text.Unicode;
using MQTTnet;
using MqttProbe.Services.Plugins.Contracts;

namespace MqttProbe.Services.Plugins.BuiltIn;

public sealed class PlainTextPayloadDetector : IPayloadDetector
{
    public string FormatId => "plaintext";
    public int Priority => 200;

    public bool CanDetect(MqttApplicationMessageReceivedEventArgs e)
    {
        var segment = e.ApplicationMessage.GetPayloadSegment();
        if (segment.Count == 0)
            return false;

        var bytes = segment.Array.AsSpan(segment.Offset, segment.Count);
        return Utf8.IsValid(bytes);
    }
}
