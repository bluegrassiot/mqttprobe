using System.Collections.Concurrent;

namespace MqttProbe.Models.Sparkplug;

public enum SpbNodeStatus { Unknown, Online, Offline }

public sealed record SpbMetricSnapshot(string Name, string DataType, string Value, DateTime LastUpdated, ulong? Alias = null);

public sealed class SpbDevice
{
    public string DeviceId { get; init; } = string.Empty;
    public string NodeId { get; init; } = string.Empty;
    public string GroupId { get; init; } = string.Empty;
    public SpbNodeStatus Status { get; set; }
    public DateTime? LastBirthAt { get; set; }
    public DateTime? LastDataAt { get; set; }
    public DateTime? LastDeathAt { get; set; }
    public IReadOnlyList<SpbMetricSnapshot> Metrics { get; internal set; } = [];
    internal Lock SyncRoot { get; } = new();
    internal Dictionary<ulong, string> AliasMap { get; } = [];
}

public sealed class SpbNode
{
    public string NodeId { get; init; } = string.Empty;
    public string GroupId { get; init; } = string.Empty;
    public SpbNodeStatus Status { get; set; }
    public DateTime? LastBirthAt { get; set; }
    public DateTime? LastDataAt { get; set; }
    public DateTime? LastDeathAt { get; set; }
    public DateTime? LastRebirthRequestAt { get; set; }
    public IReadOnlyList<SpbMetricSnapshot> Metrics { get; internal set; } = [];
    public ConcurrentDictionary<string, SpbDevice> Devices { get; } = new();
    internal Lock SyncRoot { get; } = new();
    internal Dictionary<ulong, string> AliasMap { get; } = [];
}

public sealed class SpbGroup
{
    public string GroupId { get; init; } = string.Empty;
    public ConcurrentDictionary<string, SpbNode> Nodes { get; } = new();
}
