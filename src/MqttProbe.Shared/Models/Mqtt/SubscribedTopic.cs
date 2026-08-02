using MQTTnet.Protocol;

namespace MqttProbe.Models.Mqtt;

public sealed class SubscribedTopic : IEquatable<SubscribedTopic>
{
    public string Topic { get; set; } = string.Empty;

    public MqttQualityOfServiceLevel QualityOfServiceLevel { get; set; } =
        MqttQualityOfServiceLevel.AtLeastOnce;

    public bool Equals(SubscribedTopic? other) =>
        other is not null &&
        string.Equals(Topic, other.Topic, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as SubscribedTopic);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Topic ?? string.Empty);
}
