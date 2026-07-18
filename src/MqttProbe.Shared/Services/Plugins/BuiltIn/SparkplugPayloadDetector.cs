using MQTTnet.Client;
using MqttProbe.Services.Plugins.Contracts;

namespace MqttProbe.Services.Plugins.BuiltIn;

public sealed class SparkplugPayloadDetector : IPayloadDetector
{
    public string FormatId => "sparkplug-b";
    public int Priority => 900;

    public bool CanDetect(MqttApplicationMessageReceivedEventArgs e) =>
        e.ApplicationMessage.Topic.StartsWith("spBv1.0", StringComparison.Ordinal);
}
