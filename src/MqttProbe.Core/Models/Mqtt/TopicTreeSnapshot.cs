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
    // A direct message was accepted somewhere under this node's exact path and has not since
    // been excluded or reset. Provenance is the raw topic name the message was published with,
    // so a node reached only through an alias such as "root//tip" still reports it, and an
    // exclude retires it only when it matches that raw name rather than the normalized path.
    // Retention keeps the record, so a real topic emptied by eviction stays distinct from a
    // structural prefix. Legacy six-argument snapshots fall back to HasDirectMessages.
    public bool HasObservedDirectTopic { get; init; } = HasDirectMessages;

    // Narrower than HasObservedDirectTopic: a direct message was published at this node's own
    // normalized path, rather than at an alias spelling that normalizes onto it, such as
    // "root//tip" or "root/tip/". MQTT carries those spellings, but they are not the supported
    // Sparkplug endpoint shape, so a node observed only through an alias is a prefix to group
    // under rather than a topic an endpoint sits at. Legacy six-argument snapshots fall back to
    // HasDirectMessages, the same as the general flag.
    public bool HasObservedCanonicalTopic { get; init; } = HasDirectMessages;
}

public sealed record TopicTreeSnapshot(long Version, ImmutableArray<TopicNodeSnapshot> Roots);
