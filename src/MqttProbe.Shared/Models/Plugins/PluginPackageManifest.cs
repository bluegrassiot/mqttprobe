namespace MqttProbe.Models.Plugins;

public sealed record PluginPackageManifest
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;

    public string Kind { get; init; } = string.Empty;

    public string? Description { get; init; }

    public string? Author { get; init; }

    public string? MinAppVersion { get; init; }
}

public static class PluginPackageKinds
{
    public const string ProtobufSchemas = "protobuf-schemas";

    public const string Assembly = "assembly";

    public static bool IsKnown(string? kind) =>
        kind is ProtobufSchemas or Assembly;
}
