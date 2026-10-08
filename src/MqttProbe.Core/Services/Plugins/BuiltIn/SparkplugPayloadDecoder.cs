using System.Text;
using MQTTnet;
using MqttProbe.PluginContracts;
using Org.Eclipse.Tahu.Protobuf;

namespace MqttProbe.Core.Services.Plugins.BuiltIn;

public sealed class SparkplugPayloadDecoder : IPayloadDecoder
{
    private static readonly UTF8Encoding _strictUtf8 = new(false, true);

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

        if (IsStateTopic(topic))
        {
            return DecodeState(raw, topic);
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

    // STATE is the one verb the spec does not encode as protobuf: 3.0 uses JSON with
    // 'online' and 'timestamp', 2.2 uses bare ONLINE/OFFLINE. Both are text, so the
    // payload is surfaced as-is and never handed to the protobuf parser.
    private DecodedPayloadEnvelope DecodeState(byte[] raw, string topic)
    {
        try
        {
            return DecodedPayloadEnvelope.CreateSuccess(
                FormatId, topic, raw, _strictUtf8.GetString(raw));
        }
        catch (DecoderFallbackException ex)
        {
            return DecodedPayloadEnvelope.CreateFailure(
                FormatId,
                topic,
                raw,
                $"Sparkplug STATE payload is not valid UTF-8: {ex.Message}");
        }
    }

    private static bool IsStateTopic(string topic)
    {
        if (!topic.StartsWith("spBv1.0/", StringComparison.Ordinal))
        {
            return false;
        }

        // STATE is the only topic without a group segment, so the verb sits at index 1:
        // spBv1.0/STATE/{host_id} rather than spBv1.0/{group}/{VERB}/{node_id}.
        var segments = topic.Split('/');
        return segments.Length == 3 && segments[1] == "STATE";
    }
}
