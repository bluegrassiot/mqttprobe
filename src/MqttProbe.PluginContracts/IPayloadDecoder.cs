using MQTTnet;

namespace MqttProbe.PluginContracts;

public interface IPayloadDecoder
{
    public string FormatId { get; }
    public DecodedPayloadEnvelope Decode(MqttApplicationMessageReceivedEventArgs e);
}
