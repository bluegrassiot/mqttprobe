using System.Collections.Immutable;

namespace MqttProbe.Core.Models.Mqtt;

public sealed record SelectedMessagesSnapshot(
    SelectedTopicState State,
    int Limit,
    ImmutableArray<MqttMessage> Messages);
