using MqttProbe.Core.Models.Sparkplug;
using MqttProbe.PluginContracts;

namespace MqttProbe.Core.Services.Sparkplug;

public sealed partial class SparkplugTopologyService
{
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
                node.SequenceSubscriptionRevision = null;
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

    private void MarkObservationIncompleteIfNeeded(
        string groupId, string nodeId, SpbSequenceObservationContext? observationContext)
    {
        if (observationContext is not { } context
            || !_groups.TryGetValue(groupId, out var group)
            || !group.Nodes.TryGetValue(nodeId, out var node))
            return;

        lock (node.SyncRoot)
        {
            if (!context.HasCompleteSubscriptions
                || context.ConnectionEpoch != node.SequenceConnectionEpoch
                || context.SubscriptionRevision != node.SequenceSubscriptionRevision)
                node.SequenceObservationComplete = false;
        }
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
        return accepted || !node.SequenceObservationComplete || sequence is null or > byte.MaxValue;
    }

    private SpbSequenceTracker EnsureSequenceTracker(SpbNode node) =>
        node.SequenceTracker ??= new SpbSequenceTracker(_timeProvider, _reorderTimeoutMilliseconds);
}
