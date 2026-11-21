namespace MqttProbe.Core.Services.Sparkplug;

public sealed partial class SparkplugTopologyService
{
    private void RecordNodeRebirthObservation(string groupId, string nodeId)
    {
        if (!_groups.TryGetValue(groupId, out var group) || !group.Nodes.TryGetValue(nodeId, out var node))
            return;

        lock (node.SyncRoot)
        {
            node.LastRebirthRequestAt = _timeProvider.GetUtcNow().UtcDateTime;
        }

        TopologyChanged?.Invoke();
    }
}
