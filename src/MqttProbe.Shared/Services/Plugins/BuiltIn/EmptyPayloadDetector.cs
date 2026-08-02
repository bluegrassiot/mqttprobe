using MQTTnet;
using MqttProbe.Services.Plugins.Contracts;

namespace MqttProbe.Services.Plugins.BuiltIn;

public sealed class EmptyPayloadDetector : IPayloadDetector
{
    public string FormatId => "empty";
    public int Priority => 1000;

    public bool CanDetect(MqttApplicationMessageReceivedEventArgs e) =>
        e.ApplicationMessage.GetPayloadSegment().Count == 0;
}
