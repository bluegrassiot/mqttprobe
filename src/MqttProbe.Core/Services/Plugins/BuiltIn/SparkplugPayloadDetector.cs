using MQTTnet;
using MqttProbe.PluginContracts;

namespace MqttProbe.Core.Services.Plugins.BuiltIn;

public sealed class SparkplugPayloadDetector : IPayloadDetector
{
    public string FormatId => "sparkplug-b";
    public int Priority => 900;
    public string? DisplayName => "Sparkplug B";

    public bool CanDetect(MqttApplicationMessageReceivedEventArgs e) =>
        e.ApplicationMessage.Topic.StartsWith("spBv1.0", StringComparison.Ordinal);
}
