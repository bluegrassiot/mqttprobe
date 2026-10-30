namespace MqttProbe.Core.Models.Mqtt;

public sealed record SparkplugTopicExclude(
    SparkplugSelectionScope Scope,
    string? MessageType = null);
