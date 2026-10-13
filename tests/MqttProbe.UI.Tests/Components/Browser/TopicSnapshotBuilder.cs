using System.Collections.Immutable;
using MqttProbe.Core.Models.Mqtt;

namespace MqttProbe.UI.Tests.Components.Browser;

// Builds snapshot trees with the same conventions as TopicTreeStore: TopicCount is the
// number of descendant nodes, MessageCount is the subtree aggregate including the node.
// A listed topic is an accepted endpoint, so it carries the observed marker even when
// retention has since emptied it; intermediate segments are structural prefixes.
internal static class TopicSnapshotBuilder
{
    public static ImmutableArray<TopicNodeSnapshot> Build(params (string Topic, int Messages)[] topics) =>
        Build([.. topics.Select(topic => (topic.Topic, topic.Messages, Observed: true))]);

    // For fixtures that model history: a topic whose accepted-endpoint marker was cleared (an
    // exact purge) while its descendants stay, so it is structural again.
    public static ImmutableArray<TopicNodeSnapshot> Build(params (string Topic, int Messages, bool Observed)[] topics)
    {
        var root = new Node(string.Empty, string.Empty);
        foreach (var (topic, messages, observed) in topics)
            root.Add(topic, messages, observed);

        return root.SnapshotChildren();
    }

    private sealed class Node(string topic, string fullTopic)
    {
        private readonly SortedDictionary<string, Node> _children = new(StringComparer.Ordinal);
        private int _messages;
        private bool _observed;

        public void Add(string topic, int messages, bool observed)
        {
            var current = this;
            var path = string.Empty;
            foreach (var segment in topic.Split('/'))
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
                HasObservedDirectTopic = _observed
            };
        }
    }
}
