using System.Collections.Concurrent;

namespace MqttProbe.Core.Models.Mqtt;

internal class MessageStore
{
    public ConcurrentDictionary<string, MessageStore>? SubTopics { get; set; }
    public string? Topic { get; init; }
    public string? FullTopic { get; init; }
    public MessageStore? Parent { get; set; }
    public int TopicCount { get; set; }
    public int MessageCount { get; set; }
    public ConcurrentQueue<MqttMessage>? Messages { get; set; }

    // Raw topic names of every direct message accepted at this node, kept after the messages
    // themselves are evicted. Exclusion matches these names rather than the normalized
    // FullTopic, so an alias such as "root//tip" is still retired by its own filter once its
    // message is gone. Accepted as an unbounded set for the node's lifetime: a raw name can only
    // be dropped by an exclude that matches it.
    public HashSet<string>? ObservedRawTopics { get; set; }

    public bool HasObservedDirectTopic => ObservedRawTopics is { Count: > 0 };
}
