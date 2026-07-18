using System.Text;
using MQTTnet.Client;
using MqttProbe.Services.Plugins.Contracts;

namespace MqttProbe.Services.Plugins.BuiltIn;

public sealed class Base64PayloadDecoder : IPayloadDecoder
{
    public string FormatId => "base64";

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
