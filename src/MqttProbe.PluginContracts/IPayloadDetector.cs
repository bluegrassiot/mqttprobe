using MQTTnet;

namespace MqttProbe.PluginContracts;

public interface IPayloadDetector
{
    public string FormatId { get; }
    public int Priority { get; }
    public bool CanDetect(MqttApplicationMessageReceivedEventArgs e);
}
