using System.Text.Unicode;
using MQTTnet;
using MqttProbe.PluginContracts;

namespace MqttProbe.Core.Services.Plugins.BuiltIn;

public sealed class PlainTextPayloadDetector : IPayloadDetector
{
    public string FormatId => "plaintext";
    public int Priority => 200;
    public string DisplayName => "Plain text";

    public bool CanDetect(MqttApplicationMessageReceivedEventArgs e)
    {
        var segment = e.ApplicationMessage.GetPayloadSegment();
        if (segment.Count == 0)
            return false;

        var bytes = segment.Array.AsSpan(segment.Offset, segment.Count);
        return Utf8.IsValid(bytes);
    }
}
