using MQTTnet;
using MqttProbe.PluginContracts;

namespace MqttProbe.Core.Services.Plugins.BuiltIn;

public sealed class EmptyPayloadDecoder : IPayloadDecoder
{
    public string FormatId => "empty";

    public DecodedPayloadEnvelope Decode(MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;
        var segment = e.ApplicationMessage.GetPayloadSegment();
        var raw = segment.Array is null ? [] : segment.ToArray();

        return DecodedPayloadEnvelope.CreateSuccess(
            FormatId,
            topic,
            raw,
            string.Empty);
    }
}
