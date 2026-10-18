using System.Collections.Concurrent;

namespace MqttProbe.Core.Models.Sparkplug;

public enum SpbNodeStatus { Unknown, Online, Offline }

public enum SpbSequenceState { Unknown, InOrder, Reordering, GapDetected, Invalid }

public sealed record SpbSequenceDiagnostics
{
    public SpbSequenceState State { get; init; }
    public byte? ExpectedSequence { get; init; }
    public ulong? ReceivedSequence { get; init; }
    public IReadOnlyList<byte> MissingSequences { get; init; } = [];
    public DateTime? ReorderStartedAtUtc { get; init; }
    public DateTime? ReorderDeadlineUtc { get; init; }
}

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
    public SpbSequenceDiagnostics Sequence { get; internal set; } = new();
    public ulong? BirthDeathSequenceNumber { get; internal set; }
    internal SpbSequenceTracker? SequenceTracker { get; set; }
    internal DateTime? LastBirthTimestampUtc { get; set; }
    internal bool SequenceObservationComplete { get; set; }
    internal long SequenceEpisodeId { get; set; }
    internal long? SequenceRecoveryInFlightEpisodeId { get; set; }
    internal long? SequenceRecoverySentEpisodeId { get; set; }
    internal ulong? LastBirthSequenceNumber { get; set; }
    internal long? SequenceConnectionEpoch { get; set; }
    public IReadOnlyList<SpbMetricSnapshot> Metrics { get; internal set; } = [];
    public ConcurrentDictionary<string, SpbDevice> Devices { get; } = new(StringComparer.Ordinal);
    internal Lock SyncRoot { get; } = new();
    internal Dictionary<ulong, string> AliasMap { get; } = [];
}

public sealed class SpbGroup
{
    public string GroupId { get; init; } = string.Empty;
    public ConcurrentDictionary<string, SpbNode> Nodes { get; } = new(StringComparer.Ordinal);
}
