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

    // Raw names of every direct message ever accepted here, kept after eviction so an exclude
    // retires an alias by its own spelling. Admission of new spellings is bounded upstream;
    // only a match drops a name from here.
    public HashSet<string>? ObservedRawTopics { get; set; }

    public bool HasObservedDirectTopic => ObservedRawTopics is { Count: > 0 };
}
