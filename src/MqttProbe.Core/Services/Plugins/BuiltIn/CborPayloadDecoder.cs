using System.Formats.Cbor;
using MQTTnet;
using MqttProbe.PluginContracts;

namespace MqttProbe.Core.Services.Plugins.BuiltIn;

public sealed class CborPayloadDecoder : IPayloadDecoder
{
    public string FormatId => "cbor";

    public DecodedPayloadEnvelope Decode(MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;
        var segment = e.ApplicationMessage.GetPayloadSegment();
        var raw = segment.Array is null ? [] : segment.ToArray();

        if (raw.Length == 0)
            return DecodedPayloadEnvelope.CreateSuccess(FormatId, topic, raw, string.Empty);

        try
        {
            var json = CborJsonConverter.Convert(raw);
            return DecodedPayloadEnvelope.CreateSuccess(FormatId, topic, raw, json);
        }
        catch (CborContentException)
        {
            return Fallback(topic, raw);
        }
        catch (InvalidOperationException)
        {
            return Fallback(topic, raw);
        }
        catch (ArgumentException)
        {
            return Fallback(topic, raw);
        }
        catch (OverflowException)
        {
            return Fallback(topic, raw);
        }
    }

    private DecodedPayloadEnvelope Fallback(string topic, byte[] raw) =>
        DecodedPayloadEnvelope.CreateSuccess(
            FormatId, topic, raw, Convert.ToHexString(raw).ToLowerInvariant());
}
