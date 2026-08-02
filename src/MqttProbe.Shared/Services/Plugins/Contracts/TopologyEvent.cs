namespace MqttProbe.Services.Plugins.Contracts;

public abstract class TopologyEvent
{
    public required string FormatId { get; init; }
    public required string Topic { get; init; }
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
}

public sealed class NodeBirthEvent : TopologyEvent
{
    public required string GroupId { get; init; }
    public required string NodeId { get; init; }
    public required IReadOnlyList<MetricSnapshot> Metrics { get; init; }
}

public sealed class NodeDeathEvent : TopologyEvent
{
    public required string GroupId { get; init; }
    public required string NodeId { get; init; }
}

public sealed class NodeDataEvent : TopologyEvent
{
    public required string GroupId { get; init; }
    public required string NodeId { get; init; }
    public required IReadOnlyList<MetricSnapshot> Metrics { get; init; }
}

public sealed class DeviceBirthEvent : TopologyEvent
{
    public required string GroupId { get; init; }
    public required string NodeId { get; init; }
    public required string DeviceId { get; init; }
    public required IReadOnlyList<MetricSnapshot> Metrics { get; init; }
}

public sealed class DeviceDeathEvent : TopologyEvent
{
    public required string GroupId { get; init; }
    public required string NodeId { get; init; }
    public required string DeviceId { get; init; }
}

public sealed class DeviceDataEvent : TopologyEvent
{
    public required string GroupId { get; init; }
    public required string NodeId { get; init; }
    public required string DeviceId { get; init; }
    public required IReadOnlyList<MetricSnapshot> Metrics { get; init; }
}

public sealed class MetricSnapshot
{
    public required string Name { get; init; }
    public required string DataType { get; init; }
    public required string Value { get; init; }
}
