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
    // A direct message was accepted at this exact path and has not since been excluded or
    // reset. Retention keeps it true, so a real topic emptied by eviction stays distinct from
    // a structural prefix. Legacy six-argument snapshots fall back to HasDirectMessages.
    public bool HasObservedDirectTopic { get; init; } = HasDirectMessages;
}

public sealed record TopicTreeSnapshot(long Version, ImmutableArray<TopicNodeSnapshot> Roots);
