using System.Text.Json;
using System.Text.Json.Serialization;
using MQTTnet;
using MqttProbe.Services.Plugins.Contracts;

namespace MqttProbe.Services.Plugins.Protobuf;

public sealed class ProtobufPayloadDecoder : IPayloadDecoder
{
    private static readonly JsonSerializerOptions _serializerOptions = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    private readonly ProtobufSchemaRegistry _registry;
    private readonly ProtobufWireDecoder _wireDecoder;

    public ProtobufPayloadDecoder(ProtobufSchemaRegistry registry)
    {
        _registry = registry;
        _wireDecoder = new ProtobufWireDecoder(registry);
    }

    public string FormatId => "protobuf";

    public DecodedPayloadEnvelope Decode(MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;
        var segment = e.ApplicationMessage.GetPayloadSegment();
        var raw = segment.Array is null ? [] : segment.ToArray();

        if (raw.Length == 0)
            return DecodedPayloadEnvelope.CreateSuccess(FormatId, topic, raw, string.Empty);

        if (!_registry.TryResolveByTopic(topic, out var message))
            return DecodedPayloadEnvelope.CreateFailure(FormatId, topic, raw,
                $"No protobuf schema mapped to topic '{topic}'.");

        try
        {
            var decoded = _wireDecoder.Decode(raw, message);
            var json = JsonSerializer.Serialize(decoded, _serializerOptions);
            return DecodedPayloadEnvelope.CreateSuccess(FormatId, topic, raw, json, typedPayload: decoded);
        }
        catch (Exception ex)
        {
            return DecodedPayloadEnvelope.CreateFailure(FormatId, topic, raw,
                $"Protobuf decode failed for '{message.Name}': {ex.Message}");
        }
    }
}
