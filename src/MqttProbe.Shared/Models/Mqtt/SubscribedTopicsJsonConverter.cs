using System.Text.Json;
using System.Text.Json.Serialization;
using MQTTnet.Protocol;

namespace MqttProbe.Models.Mqtt;

public sealed class SubscribedTopicsJsonConverter : JsonConverter<List<SubscribedTopic>>
{
    public override List<SubscribedTopic> Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return [];

        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("Expected start of array for subscribedTopics.");

        var list = new List<SubscribedTopic>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
                break;

            if (reader.TokenType == JsonTokenType.String)
            {
                list.Add(new SubscribedTopic
                {
                    Topic = reader.GetString() ?? string.Empty,
                    QualityOfServiceLevel = MqttQualityOfServiceLevel.AtLeastOnce
                });
                continue;
            }

            if (reader.TokenType == JsonTokenType.StartObject)
            {
                var entry = JsonSerializer.Deserialize<SubscribedTopic>(ref reader, options)
                            ?? new SubscribedTopic();
                list.Add(entry);
                continue;
            }

            throw new JsonException($"Unexpected token {reader.TokenType} in subscribedTopics.");
        }

        return list;
    }

    public override void Write(
        Utf8JsonWriter writer,
        List<SubscribedTopic> value,
        JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var entry in value)
            JsonSerializer.Serialize(writer, entry, options);
        writer.WriteEndArray();
    }
}
