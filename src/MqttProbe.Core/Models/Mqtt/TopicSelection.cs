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

// Key names the row and Topics are its real paths; a Scope supersedes that snapshot by
// resolving the row against the store as it is now.
public sealed record TopicSelection(
    string Key,
    string Label,
    ImmutableArray<string> Topics,
    SparkplugSelectionScope? Scope = null)
{
    public static TopicSelection ForTopic(string fullTopic) =>
        new(fullTopic, fullTopic, [fullTopic]);
}
