using System.Text.Unicode;
using MQTTnet.Client;
using MqttProbe.Services.Plugins.Contracts;

namespace MqttProbe.Services.Plugins.BuiltIn;

public sealed class JsonPayloadDetector : IPayloadDetector
{
    public string FormatId => "json";
    public int Priority => 600;

    public bool CanDetect(MqttApplicationMessageReceivedEventArgs e)
    {
        var segment = e.ApplicationMessage.PayloadSegment;
        if (segment.Count == 0)
            return false;

        var bytes = segment.Array.AsSpan(segment.Offset, segment.Count);
        if (!Utf8.IsValid(bytes))
            return false;

        var first = bytes[0];
        var last = bytes[^1];
        return (first == '{' && last == '}') || (first == '[' && last == ']');
    }
}
