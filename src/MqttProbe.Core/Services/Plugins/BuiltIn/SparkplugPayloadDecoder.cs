using System.Text;
using MQTTnet;
using MqttProbe.PluginContracts;
using Org.Eclipse.Tahu.Protobuf;

namespace MqttProbe.Core.Services.Plugins.BuiltIn;

public sealed class SparkplugPayloadDecoder : IPayloadDecoder
{
    public string FormatId => "sparkplug-b";

    public DecodedPayloadEnvelope Decode(MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;
        var segment = e.ApplicationMessage.GetPayloadSegment();
        var raw = segment.Array is null ? [] : segment.ToArray();

        if (raw.Length == 0)
        {
            return DecodedPayloadEnvelope.CreateSuccess(
                FormatId, topic, raw, string.Empty);
        }

        try
        {
            var payload = Payload.Parser.ParseFrom(raw);
            return DecodedPayloadEnvelope.CreateSuccess(
                FormatId,
                topic,
                raw,
                payload.ToString(),
                typedPayload: payload);
        }
        catch (Exception ex)
        {
            return DecodedPayloadEnvelope.CreateFailure(
                FormatId,
                topic,
                raw,
                $"Sparkplug protobuf parse failed: {ex.Message}");
        }
    }
}
