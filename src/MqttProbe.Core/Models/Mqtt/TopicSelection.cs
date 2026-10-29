using System.Collections.Immutable;
using System.Text;

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
    public TopicSelectionMode Mode { get; init; } = TopicSelectionMode.SingleTopic;

    public static TopicSelection ForTopic(string fullTopic) =>
        new(fullTopic, fullTopic, [fullTopic]);

    public static TopicSelection ForExactTopics(IEnumerable<string> fullTopics)
    {
        var paths = fullTopics.Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        var label = paths.Length == 1 ? paths[0] : $"{paths.Length} topics selected";
        return new(ComposeExactKey(paths), label, [.. paths]) { Mode = TopicSelectionMode.MultipleTopics };
    }

    internal static string ComposeExactKey(IEnumerable<string> paths)
    {
        var builder = new StringBuilder("multi:");
        foreach (var path in paths.Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal))
            builder.Append(path.Length).Append(':').Append(path);

        return builder.ToString();
    }
}
