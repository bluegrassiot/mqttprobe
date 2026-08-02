namespace MqttProbe.Models.Plugins;

public sealed class ProtobufSchemaManifest
{
    public List<ProtobufSchemaMapping> Schemas { get; init; } = [];
}

public sealed class ProtobufSchemaMapping
{
    public List<string> Files { get; init; } = [];

    public string TopicPattern { get; init; } = string.Empty;

    public string MessageType { get; init; } = string.Empty;
}

public sealed record ProtobufSchemaSource(ProtobufSchemaManifest Manifest, string SchemaRoot);
