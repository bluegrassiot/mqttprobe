using System.Collections.Immutable;

namespace MqttProbe.Core.Models.Mqtt;

public sealed record TopicNodeSnapshot(
    string Topic,
    string FullTopic,
    int TopicCount,
    int MessageCount,
    bool HasDirectMessages,
    ImmutableArray<TopicNodeSnapshot> Children)
{
    // A direct message was accepted at this node's path and has not been excluded or reset;
    // provenance is the raw published name, so an alias spelling still reports it.
    public bool HasObservedDirectTopic { get; init; } = HasDirectMessages;

    // Narrower than HasObservedDirectTopic: the message was published at this node's own
    // normalized path, not at an alias spelling that normalizes onto it.
    public bool HasObservedCanonicalTopic { get; init; } = HasDirectMessages;
}

public sealed record TopicTreeSnapshot(long Version, ImmutableArray<TopicNodeSnapshot> Roots);
