using MQTTnet;

namespace MqttProbe.Services.Plugins.Contracts;

public interface IPayloadDecoder
{
    public string FormatId { get; }
    public DecodedPayloadEnvelope Decode(MqttApplicationMessageReceivedEventArgs e);
}
