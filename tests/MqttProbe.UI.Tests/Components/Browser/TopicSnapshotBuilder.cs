using System.Collections.Immutable;
using MqttProbe.Core.Models.Mqtt;

namespace MqttProbe.UI.Tests.Components.Browser;

internal static class TopicSnapshotBuilder
{
    public static ImmutableArray<TopicNodeSnapshot> Build(params (string Topic, int Messages)[] topics) =>
        Build([.. topics.Select(topic => (topic.Topic, topic.Messages, Observed: true, Canonical: true))]);

    public static ImmutableArray<TopicNodeSnapshot> Build(params (string Topic, int Messages, bool Observed)[] topics) =>
        Build([.. topics.Select(topic => (topic.Topic, topic.Messages, topic.Observed, Canonical: topic.Observed))]);

    // The short overloads assume every listed topic is canonical, so an alias fixture has to
    // state Canonical here.
    public static ImmutableArray<TopicNodeSnapshot> Build(
        params (string Topic, int Messages, bool Observed, bool Canonical)[] topics)
    {
        var root = new Node(string.Empty, string.Empty);
        foreach (var (topic, messages, observed, canonical) in topics)
            root.Add(topic, messages, observed, canonical);

        return root.SnapshotChildren();
    }

    private static string Normalize(string topic)
    {
        var adapter = TopicTreeSelectionTestAdapter.Create(maxStoredMessages: 0);
        adapter.Add(topic, new MqttMessage("payload", "unused", false,
            MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce)
        { Topic = topic });
        var node = adapter.Store.GetTopicTreeSnapshot().Roots.Single();
        while (!node.Children.IsDefaultOrEmpty)
            node = node.Children.Single();
        return node.FullTopic;
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
            var segments = normalized.Split('/');
            for (var index = 0; index < segments.Length; index++)
            {
                var segment = segments[index];
                // The path is the joined prefix, so a leading empty level keeps its slash
                // instead of being read as "no path yet".
                var path = string.Join('/', segments.Take(index + 1));
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
