using System.Collections.Immutable;

namespace MqttProbe.Core.Models.Mqtt;

public sealed record TopicNodeSnapshot(
    string Topic,
    string FullTopic,
    int TopicCount,
    int MessageCount,
    bool HasDirectMessages,
    ImmutableArray<TopicNodeSnapshot> Children);

public sealed record TopicTreeSnapshot(long Version, ImmutableArray<TopicNodeSnapshot> Roots);
