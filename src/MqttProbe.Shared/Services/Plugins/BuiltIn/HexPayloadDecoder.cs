using System.Text;
using MQTTnet.Client;
using MqttProbe.Services.Plugins.Contracts;

namespace MqttProbe.Services.Plugins.BuiltIn;

public sealed class HexPayloadDecoder : IPayloadDecoder
{
    public string FormatId => "hex";

    public DecodedPayloadEnvelope Decode(MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;
        var segment = e.ApplicationMessage.PayloadSegment;
        var raw = segment.Array is null ? [] : segment.ToArray();

        return DecodedPayloadEnvelope.CreateSuccess(
            FormatId,
            topic,
            raw,
            Encoding.UTF8.GetString(raw));
    }
}
