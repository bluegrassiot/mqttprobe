using System.Collections.Immutable;

namespace MqttProbe.Core.Models.Mqtt;

public enum SparkplugSelectionKind
{
    Node,
    Devices,
    Device
}

public sealed record SparkplugSelectionScope(
    SparkplugSelectionKind Kind,
    string GroupPath,
    string NodeId,
    string? DeviceId = null);

public sealed record TopicSelection(
    string Key,
    string Label,
    ImmutableArray<string> Topics,
    SparkplugSelectionScope? Scope = null,
    bool RequireCanonicalTopics = false)
{
    public static TopicSelection ForTopic(string fullTopic) =>
        new(fullTopic, fullTopic, [fullTopic]);
}
