namespace MqttProbe.Core.Models.Mqtt;

public enum TopicBranchKind
{
    Raw = 0,
    Sparkplug = 1
}

public sealed record TopicBranchTarget(
    TopicBranchKind Kind,
    string? Path = null,
    SparkplugSelectionScope? Scope = null,
    string? MessageType = null);
