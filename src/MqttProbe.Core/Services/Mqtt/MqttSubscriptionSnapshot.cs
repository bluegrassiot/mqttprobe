namespace MqttProbe.Core.Services.Mqtt;

public sealed record MqttSubscriptionSnapshot(
    long ConnectionEpoch,
    long Revision,
    IReadOnlyList<string> ConfirmedFilters);
