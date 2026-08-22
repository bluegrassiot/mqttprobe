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
    public int RemoveOfflineNodes();
    public void ClearAll();
    public Task ApplyTopologyEventsAsync(IReadOnlyList<TopologyEvent> events);
}

public sealed class SparkplugTopologyService : ISparkplugTopologyService
{
    private static readonly string[] _nodeVerbs = ["NBIRTH", "NDATA", "NDEATH"];
    private static readonly string[] _deviceVerbs = ["DBIRTH", "DDATA", "DDEATH"];
    private readonly ConcurrentDictionary<string, SpbGroup> _groups = new(StringComparer.Ordinal);

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

        if (removed > 0)
            TopologyChanged?.Invoke();

        return removed;
    }

    public void ClearAll()
    {
        var wasEmpty = _groups.IsEmpty;
        _groups.Clear();
        if (!wasEmpty)
            TopologyChanged?.Invoke();
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

    private void HandleNDeath(string groupId, string nodeId)
    {
        var node = GetOrCreateNode(groupId, nodeId);
        var now = DateTime.UtcNow;
        lock (node.SyncRoot)
        {
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
                    ApplyNodeBirth(e.GroupId, e.NodeId, ConvertMetricSnapshots(e.Metrics));
                    break;
                case NodeDeathEvent e:
                    HandleNDeath(e.GroupId, e.NodeId);
                    break;
                case NodeDataEvent e:
                    ApplyNodeData(e.GroupId, e.NodeId, ConvertMetricSnapshots(e.Metrics));
                    break;
                case DeviceBirthEvent e:
                    ApplyDeviceBirth(e.GroupId, e.NodeId, e.DeviceId, ConvertMetricSnapshots(e.Metrics));
                    break;
                case DeviceDeathEvent e:
                    HandleDDeath(e.GroupId, e.NodeId, e.DeviceId);
                    break;
                case DeviceDataEvent e:
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

    private void ApplyNodeBirth(string groupId, string nodeId, SpbMetricSnapshot[] metrics)
    {
        var node = GetOrCreateNode(groupId, nodeId);
        lock (node.SyncRoot)
        {
            node.Status = SpbNodeStatus.Online;
            node.LastBirthAt = DateTime.UtcNow;
            node.AliasMap.Clear();
            PopulateAliasMap(node.AliasMap, metrics);
            node.Metrics = metrics;
        }

        TopologyChanged?.Invoke();
    }

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
            var metrics = node.Metrics.ToList();

            foreach (var snapshot in newMetrics)
            {
                var idx = metrics.FindIndex(m => m.Name == snapshot.Name);
                if (idx >= 0)
                    metrics[idx] = snapshot;
                else
                    metrics.Add(snapshot);
            }

            node.Metrics = metrics.ToArray();
        }

        TopologyChanged?.Invoke();
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
            var metrics = device.Metrics.ToList();

            foreach (var snapshot in newMetrics)
            {
                var idx = metrics.FindIndex(m => m.Name == snapshot.Name);
                if (idx >= 0)
                    metrics[idx] = snapshot;
                else
                    metrics.Add(snapshot);
            }

            device.Metrics = metrics.ToArray();
        }

        TopologyChanged?.Invoke();
    }
}
