using System.Collections.Immutable;

namespace MqttProbe.Core.Models.Mqtt;

// A topic-tree row's selection. Key identifies the row, which may be a real topic path or a
// caller-defined grouping id that no topic can collide with. Label is the name a caller shows.
// Topics are the real paths whose subtrees the selection covers: one for a topic row, several for
// a grouped row such as the devices under a node.
public sealed record TopicSelection(string Key, string Label, ImmutableArray<string> Topics)
{
    public static TopicSelection ForTopic(string fullTopic) =>
        new(fullTopic, fullTopic, [fullTopic]);
}
