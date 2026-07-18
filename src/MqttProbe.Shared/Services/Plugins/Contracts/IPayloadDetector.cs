using MQTTnet.Client;

namespace MqttProbe.Services.Plugins.Contracts;

public interface IPayloadDetector
{
    public string FormatId { get; }
    public int Priority { get; }
    public bool CanDetect(MqttApplicationMessageReceivedEventArgs e);
}
