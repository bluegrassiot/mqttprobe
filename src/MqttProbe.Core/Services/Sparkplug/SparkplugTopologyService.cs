using System.Collections.Concurrent;
using MqttProbe.Core.Models.Sparkplug;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.PluginContracts;

namespace MqttProbe.Core.Services.Sparkplug;

public interface ISparkplugTopologyService
{
    public IReadOnlyDictionary<string, SpbGroup> Groups { get; }
    public event Action? TopologyChanged;
    public bool RemoveNode(string groupId, string nodeId);
    public int RemoveMatchingTopic(string filter);

    // Exclusion purgers run while the exclude service holds its locks; a subscriber that
    // re-enters the service from TopologyChanged would deadlock, so the purge removes
    // silently and the caller raises the event later through RaiseTopologyChanged.
    public int RemoveMatchingTopicSilent(string filter);
    public void RaiseTopologyChanged();

    public int RemoveOfflineNodes();
    public void ClearAll();
    public Task ApplyTopologyEventsAsync(IReadOnlyList<TopologyEvent> events);
    public int CheckSequenceTimeouts() => 0;
    public void ResetSequenceTracking() { }
    public void MarkSequenceObservationIncomplete(string topic) { }
    public void RecordSequenceConnectionEpoch(IReadOnlyList<TopologyEvent> events, long? connectionEpoch) { }
}

public sealed class SparkplugTopologyService(
    TimeProvider? timeProvider = null,
    int reorderTimeoutMilliseconds = 5000,
    MqttProbe.Core.Services.Configuration.ISparkplugSettings? sparkplugSettings = null) : ISparkplugTopologyService
{
    private static readonly string[] _nodeVerbs = ["NBIRTH", "NDATA", "NDEATH"];
    private static readonly string[] _deviceVerbs = ["DBIRTH", "DDATA", "DDEATH"];
    private readonly ConcurrentDictionary<string, SpbGroup> _groups = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly int _reorderTimeoutMilliseconds = Math.Clamp(reorderTimeoutMilliseconds, 0, 60000);

    public IReadOnlyDictionary<string, SpbGroup> Groups => _groups;
    public event Action? TopologyChanged;

    public bool RemoveNode(string groupId, string nodeId)
    {
        if (!_groups.TryGetValue(groupId, out var group))
            return false;

        if (!group.Nodes.TryRemove(nodeId, out _))
            return false;

        if (group.Nodes.IsEmpty)
            _groups.TryRemove(groupId, out _);

        TopologyChanged?.Invoke();
        return true;
    }

    public int RemoveOfflineNodes()
    {
        var removed = 0;

        foreach (var (groupId, group) in _groups)
        {
            foreach (var (nodeId, node) in group.Nodes)
            {
                if (node.Status == SpbNodeStatus.Offline && group.Nodes.TryRemove(nodeId, out _))
                    removed++;
            }

            if (group.Nodes.IsEmpty)
                _groups.TryRemove(groupId, out _);
        }

        if (removed > 0)
            TopologyChanged?.Invoke();

        return removed;
    }

    public int RemoveMatchingTopic(string filter)
    {
        var removed = RemoveMatchingTopicCore(filter);
        if (removed > 0)
            TopologyChanged?.Invoke();

        return removed;
    }

    public int RemoveMatchingTopicSilent(string filter) => RemoveMatchingTopicCore(filter);

    public void RaiseTopologyChanged() => TopologyChanged?.Invoke();

    private int RemoveMatchingTopicCore(string filter)
    {
        var removed = 0;

        foreach (var (groupId, group) in _groups)
        {
            foreach (var (nodeId, node) in group.Nodes)
            {
                foreach (var (deviceId, _) in node.Devices)
                {
                    if (TopicMatchesDevice(filter, groupId, nodeId, deviceId)
                        && node.Devices.TryRemove(deviceId, out _))
                    {
                        removed++;
                    }
                }

                if (TopicMatchesNode(filter, groupId, nodeId)
                    && group.Nodes.TryRemove(nodeId, out _))
                {
                    removed++;
                }
            }

            if (group.Nodes.IsEmpty && _groups.TryRemove(groupId, out _))
                removed++;
        }

        return removed;
    }

    public void ClearAll()
    {
        var wasEmpty = _groups.IsEmpty;
        _groups.Clear();
        if (!wasEmpty)
            TopologyChanged?.Invoke();
    }

    public int CheckSequenceTimeouts()
    {
        var changed = 0;
        foreach (var node in _groups.Values.SelectMany(group => group.Nodes.Values))
        {
            lock (node.SyncRoot)
            {
                node.SequenceTracker?.UpdateTimeout(sparkplugSettings?.Sparkplug.ReorderTimeoutMilliseconds ?? _reorderTimeoutMilliseconds);
                if (node.SequenceTracker?.CheckTimeout() != true)
                    continue;
                node.Sequence = node.SequenceTracker.Snapshot;
                changed++;
            }
        }

        if (changed > 0)
            TopologyChanged?.Invoke();
        return changed;
    }

    public void ResetSequenceTracking()
    {
        foreach (var node in _groups.Values.SelectMany(group => group.Nodes.Values))
        {
            lock (node.SyncRoot)
            {
                node.SequenceTracker?.Reset(null);
                node.Sequence = node.SequenceTracker?.Snapshot ?? new SpbSequenceDiagnostics();
                node.SequenceRecoveryInFlightEpisodeId = null;
                node.SequenceRecoverySentEpisodeId = null;
                node.SequenceObservationComplete = false;
                node.SequenceConnectionEpoch = null;
                node.LastBirthSequenceNumber = null;
                node.LastBirthTimestampUtc = null;
                node.SequenceEpisodeId++;
            }
        }
    }

    public void RecordSequenceConnectionEpoch(IReadOnlyList<TopologyEvent> events, long? connectionEpoch)
    {
        foreach (var evt in events)
        {
            var (groupId, nodeId) = evt switch
            {
                NodeBirthEvent birth => (birth.GroupId, birth.NodeId),
                NodeDataEvent data => (data.GroupId, data.NodeId),
                DeviceBirthEvent birth => (birth.GroupId, birth.NodeId),
                DeviceDataEvent data => (data.GroupId, data.NodeId),
                DeviceDeathEvent death => (death.GroupId, death.NodeId),
                _ => (null, null)
            };

            if (groupId is null || nodeId is null
                || !_groups.TryGetValue(groupId, out var group)
                || !group.Nodes.TryGetValue(nodeId, out var node))
                continue;

            lock (node.SyncRoot)
                node.SequenceConnectionEpoch = connectionEpoch;
        }
    }

    public void MarkSequenceObservationIncomplete(string topic)
    {
        if (!TryParseTopic(topic, out var groupId, out var verb, out var nodeId, out _)
            || verb is not ("NBIRTH" or "DBIRTH" or "NDATA" or "DDATA" or "DDEATH")
            || !_groups.TryGetValue(groupId, out var group)
            || !group.Nodes.TryGetValue(nodeId, out var node))
            return;

        lock (node.SyncRoot)
            node.SequenceObservationComplete = false;
    }

    private static bool TopicMatchesNode(string filter, string groupId, string nodeId) =>
        _nodeVerbs
            .Any(verb => TopicExcludeService.Matches($"spBv1.0/{groupId}/{verb}/{nodeId}", filter));

    private static bool TopicMatchesDevice(string filter, string groupId, string nodeId, string deviceId) =>
        _deviceVerbs
            .Any(verb => TopicExcludeService.Matches($"spBv1.0/{groupId}/{verb}/{nodeId}/{deviceId}", filter));

    internal static bool TryParseTopic(string topic, out string group, out string verb, out string node, out string? device)
    {
        group = verb = node = string.Empty;
        device = null;

        if (!topic.StartsWith("spBv1.0/", StringComparison.Ordinal))
            return false;

        var segments = topic.Split('/');

        if (segments.Length < 4)
            return false;

        verb = segments[2];
        if (verb == "STATE")
            return false;

        group = segments[1];
        node = segments[3];
        device = segments.Length >= 5 ? segments[4] : null;
        return true;
    }

    private SpbNode GetOrCreateNode(string groupId, string nodeId)
    {
        var group = _groups.GetOrAdd(groupId, id => new SpbGroup { GroupId = id });
        return group.Nodes.GetOrAdd(nodeId, id => new SpbNode { NodeId = id, GroupId = groupId });
    }

    private void HandleNDeath(string groupId, string nodeId, ulong? birthDeathSequenceNumber)
    {
        var node = GetOrCreateNode(groupId, nodeId);
        var now = DateTime.UtcNow;
        lock (node.SyncRoot)
        {
            if (birthDeathSequenceNumber is { } deathSequence
                && node.BirthDeathSequenceNumber is { } currentSequence
                && deathSequence != currentSequence)
                return;
            if (node.BirthDeathSequenceNumber is { } activeSequence
                && birthDeathSequenceNumber != activeSequence)
                return;
            node.SequenceTracker?.Reset(null);
            node.Sequence = node.SequenceTracker?.Snapshot ?? new SpbSequenceDiagnostics();
            node.SequenceEpisodeId++;
            node.SequenceRecoveryInFlightEpisodeId = null;
            node.SequenceRecoverySentEpisodeId = null;
            node.SequenceObservationComplete = false;
            node.SequenceConnectionEpoch = null;
            node.LastBirthSequenceNumber = null;
            node.LastBirthTimestampUtc = null;
            node.Status = SpbNodeStatus.Offline;
            node.LastDeathAt = now;
        }

        foreach (var device in node.Devices.Values)
        {
            lock (device.SyncRoot)
            {
                device.Status = SpbNodeStatus.Offline;
                device.LastDeathAt ??= now;
            }
        }

        TopologyChanged?.Invoke();
    }

    private void HandleDDeath(string groupId, string nodeId, string deviceId)
    {
        var node = GetOrCreateNode(groupId, nodeId);
        var device = node.Devices.GetOrAdd(deviceId, id => new SpbDevice
        {
            DeviceId = id,
            NodeId = nodeId,
            GroupId = groupId
        });

        lock (device.SyncRoot)
        {
            device.Status = SpbNodeStatus.Offline;
            device.LastDeathAt = DateTime.UtcNow;
        }

        TopologyChanged?.Invoke();
    }

    public Task ApplyTopologyEventsAsync(IReadOnlyList<TopologyEvent> events)
    {
        foreach (var evt in events)
        {
            switch (evt)
            {
                case NodeBirthEvent e:
                    ApplyNodeBirth(e, ConvertMetricSnapshots(e.Metrics));
                    break;
                case NodeDeathEvent e:
                    HandleNDeath(e.GroupId, e.NodeId, e.BirthDeathSequenceNumber);
                    break;
                case NodeDataEvent e:
                    if (ObserveSequence(e.GroupId, e.NodeId, e.SequenceNumber))
                        ApplyNodeData(e.GroupId, e.NodeId, ConvertMetricSnapshots(e.Metrics));
                    break;
                case DeviceBirthEvent e:
                    if (ObserveSequence(e.GroupId, e.NodeId, e.SequenceNumber))
                        ApplyDeviceBirth(e.GroupId, e.NodeId, e.DeviceId, ConvertMetricSnapshots(e.Metrics));
                    break;
                case DeviceDeathEvent e:
                    if (ObserveSequence(e.GroupId, e.NodeId, e.SequenceNumber))
                        HandleDDeath(e.GroupId, e.NodeId, e.DeviceId);
                    break;
                case DeviceDataEvent e:
                    if (ObserveSequence(e.GroupId, e.NodeId, e.SequenceNumber))
                        ApplyDeviceData(e.GroupId, e.NodeId, e.DeviceId, ConvertMetricSnapshots(e.Metrics));
                    break;
            }
        }

        return Task.CompletedTask;
    }

    private static SpbMetricSnapshot[] ConvertMetricSnapshots(IReadOnlyList<MetricSnapshot> metrics)
    {
        var result = new SpbMetricSnapshot[metrics.Count];
        for (var i = 0; i < metrics.Count; i++)
        {
            var m = metrics[i];
            result[i] = new SpbMetricSnapshot(m.Name, m.DataType, m.Value, DateTime.UtcNow, m.Alias);
        }

        return result;
    }

    private void ApplyNodeBirth(NodeBirthEvent evt, SpbMetricSnapshot[] metrics)
    {
        var node = GetOrCreateNode(evt.GroupId, evt.NodeId);
        lock (node.SyncRoot)
        {
            var duplicate = evt.SequenceNumber is { } sequence
                && node.LastBirthSequenceNumber == sequence
                && node.LastBirthTimestampUtc == evt.TimestampUtc
                && node.BirthDeathSequenceNumber == evt.BirthDeathSequenceNumber;
            if (!duplicate)
            {
                EnsureSequenceTracker(node).Reset(evt.SequenceNumber);
                node.Sequence = node.SequenceTracker!.Snapshot;
                node.SequenceEpisodeId++;
                node.SequenceRecoveryInFlightEpisodeId = null;
                node.SequenceRecoverySentEpisodeId = null;
                node.SequenceObservationComplete = evt.SequenceNumber is { } baseline && baseline <= byte.MaxValue;
                node.BirthDeathSequenceNumber = evt.BirthDeathSequenceNumber;
                node.LastBirthSequenceNumber = evt.SequenceNumber;
                node.LastBirthTimestampUtc = evt.TimestampUtc;
            }
            node.Status = SpbNodeStatus.Online;
            node.LastBirthAt = DateTime.UtcNow;
            node.AliasMap.Clear();
            PopulateAliasMap(node.AliasMap, metrics);
            node.Metrics = metrics;
        }

        TopologyChanged?.Invoke();
    }

    private bool ObserveSequence(string groupId, string nodeId, ulong? sequence)
    {
        var node = GetOrCreateNode(groupId, nodeId);
        bool accepted;
        lock (node.SyncRoot)
        {
            var tracker = EnsureSequenceTracker(node);
            var wasInOrder = node.Sequence.State == SpbSequenceState.InOrder;
            accepted = tracker.Observe(sequence);
            node.Sequence = tracker.Snapshot;
            if (wasInOrder && node.Sequence.State == SpbSequenceState.Reordering)
                node.SequenceEpisodeId++;
            if (node.Sequence.State == SpbSequenceState.InOrder)
                node.SequenceRecoverySentEpisodeId = null;
        }

        if (!accepted)
            TopologyChanged?.Invoke();
        return accepted || sequence is null or > byte.MaxValue;
    }

    private SpbSequenceTracker EnsureSequenceTracker(SpbNode node) =>
        node.SequenceTracker ??= new SpbSequenceTracker(_timeProvider, _reorderTimeoutMilliseconds);

    private static void PopulateAliasMap(Dictionary<ulong, string> aliasMap, SpbMetricSnapshot[] metrics)
    {
        foreach (var metric in metrics)
        {
            if (metric.Alias is { } alias)
                aliasMap[alias] = metric.Name;
        }
    }

    private void ApplyNodeData(string groupId, string nodeId, SpbMetricSnapshot[] newMetrics)
    {
        var node = GetOrCreateNode(groupId, nodeId);
        lock (node.SyncRoot)
        {
            node.LastDataAt = DateTime.UtcNow;
            node.Metrics = MergeMetricSnapshots(node.Metrics, newMetrics);
        }

        TopologyChanged?.Invoke();
    }

    // Preserves the previous order: existing metrics are replaced in place, new names appended.
    private static SpbMetricSnapshot[] MergeMetricSnapshots(
        IReadOnlyList<SpbMetricSnapshot> existing, SpbMetricSnapshot[] incoming)
    {
        var metrics = existing.ToList();
        var index = new Dictionary<string, int>(metrics.Count, StringComparer.Ordinal);
        for (var i = 0; i < metrics.Count; i++)
            index.TryAdd(metrics[i].Name, i);

        foreach (var snapshot in incoming)
        {
            if (index.TryGetValue(snapshot.Name, out var idx))
            {
                metrics[idx] = snapshot;
            }
            else
            {
                index[snapshot.Name] = metrics.Count;
                metrics.Add(snapshot);
            }
        }

        return metrics.ToArray();
    }

    private void ApplyDeviceBirth(string groupId, string nodeId, string deviceId, SpbMetricSnapshot[] metrics)
    {
        var node = GetOrCreateNode(groupId, nodeId);
        var device = node.Devices.GetOrAdd(deviceId, id => new SpbDevice
        {
            DeviceId = id,
            NodeId = nodeId,
            GroupId = groupId
        });

        lock (device.SyncRoot)
        {
            device.Status = SpbNodeStatus.Online;
            device.LastBirthAt = DateTime.UtcNow;
            device.AliasMap.Clear();
            PopulateAliasMap(device.AliasMap, metrics);
            device.Metrics = metrics;
        }

        TopologyChanged?.Invoke();
    }

    private void ApplyDeviceData(string groupId, string nodeId, string deviceId, SpbMetricSnapshot[] newMetrics)
    {
        var node = GetOrCreateNode(groupId, nodeId);
        var device = node.Devices.GetOrAdd(deviceId, id => new SpbDevice
        {
            DeviceId = id,
            NodeId = nodeId,
            GroupId = groupId
        });

        lock (device.SyncRoot)
        {
            device.LastDataAt = DateTime.UtcNow;
            device.Metrics = MergeMetricSnapshots(device.Metrics, newMetrics);
        }

        TopologyChanged?.Invoke();
    }
}
