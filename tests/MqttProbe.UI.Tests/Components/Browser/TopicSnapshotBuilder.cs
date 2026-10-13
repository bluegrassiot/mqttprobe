using System.Collections.Immutable;
using MqttProbe.Core.Models.Mqtt;

namespace MqttProbe.UI.Tests.Components.Browser;

// Builds snapshot trees with the same conventions as TopicTreeStore: TopicCount is the
// number of descendant nodes, MessageCount is the subtree aggregate including the node.
// A listed topic is an accepted endpoint, so it carries the observed marker even when
// retention has since emptied it; intermediate segments are structural prefixes.
//
// Topics are normalized the way the store normalizes a published topic before it is filed:
// leading, trailing and repeated separators collapse. That matters for the observed markers,
// because TopicTreeStore records the raw published spelling alongside them, and a node published
// at an alias spelling such as "spBv1.0/g/NDATA//n1" is observed but is not a supported endpoint
// shape. The short overloads below treat every listed topic as canonical, so a fixture that
// models an alias must use the overload taking Canonical and state it.
internal static class TopicSnapshotBuilder
{
    public static ImmutableArray<TopicNodeSnapshot> Build(params (string Topic, int Messages)[] topics) =>
        Build([.. topics.Select(topic => (topic.Topic, topic.Messages, Observed: true, Canonical: true))]);

    // For fixtures that model history: a topic whose accepted-endpoint marker was cleared (an
    // exact purge) while its descendants stay, so it is structural again. An observed topic
    // counts as canonical here, so an alias needs the overload that states Canonical.
    public static ImmutableArray<TopicNodeSnapshot> Build(params (string Topic, int Messages, bool Observed)[] topics) =>
        Build([.. topics.Select(topic => (topic.Topic, topic.Messages, topic.Observed, Canonical: topic.Observed))]);

    // Canonical is stated rather than derived from the spelling, so a fixture can model a node
    // that was observed both canonically and through an alias, which is what a mixed history
    // looks like once both spellings have been published at it.
    public static ImmutableArray<TopicNodeSnapshot> Build(
        params (string Topic, int Messages, bool Observed, bool Canonical)[] topics)
    {
        var root = new Node(string.Empty, string.Empty);
        foreach (var (topic, messages, observed, canonical) in topics)
            root.Add(topic, messages, observed, canonical);

        return root.SnapshotChildren();
    }

    // Mirrors TopicTreeStore.Normalize: a published topic is trimmed at the ends and its
    // separators collapse, so "a//b/" and "a/b" file under the same node.
    private static string Normalize(string topic)
    {
        var segments = topic.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('/', segments);
    }

    private sealed class Node(string topic, string fullTopic)
    {
        private readonly SortedDictionary<string, Node> _children = new(StringComparer.Ordinal);
        private int _messages;
        private bool _observed;
        private bool _canonical;

        public void Add(string topic, int messages, bool observed, bool canonical)
        {
            var normalized = Normalize(topic);
            var current = this;
            var path = string.Empty;
            foreach (var segment in normalized.Split('/'))
            {
                path = path.Length == 0 ? segment : path + "/" + segment;
                if (!current._children.TryGetValue(segment, out var child))
                {
                    child = new Node(segment, path);
                    current._children[segment] = child;
                }

                current = child;
            }

            current._messages += messages;
            current._observed |= observed;
            current._canonical |= canonical && observed;
        }

        public ImmutableArray<TopicNodeSnapshot> SnapshotChildren()
        {
            var builder = ImmutableArray.CreateBuilder<TopicNodeSnapshot>(_children.Count);
            foreach (var child in _children.Values)
                builder.Add(child.Snapshot());

            return builder.ToImmutable();
        }

        private TopicNodeSnapshot Snapshot()
        {
            var children = SnapshotChildren();
            var topicCount = 0;
            var messageCount = _messages;
            foreach (var child in children)
            {
                topicCount += child.TopicCount + 1;
                messageCount += child.MessageCount;
            }

            return new TopicNodeSnapshot(topic, fullTopic, topicCount, messageCount, _messages > 0, children)
            {
                HasObservedDirectTopic = _observed,
                HasObservedCanonicalTopic = _canonical
            };
        }
    }
}
