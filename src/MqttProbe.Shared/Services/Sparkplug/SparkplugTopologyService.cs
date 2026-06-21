using System.Collections.Concurrent;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;
using MQTTnet.Protocol;
using MqttProbe.Models.Sparkplug;
using Org.Eclipse.Tahu.Protobuf;

namespace MqttProbe.Services.Sparkplug;

public interface ISparkplugTopologyService : IDisposable
{
    public IReadOnlyDictionary<string, SpbGroup> Groups { get; }
    public event Action? TopologyChanged;
    public bool RemoveNode(string groupId, string nodeId);
    public int RemoveOfflineNodes();
    public Task RequestNodeRebirthAsync(string groupId, string nodeId);
}

public sealed class SparkplugTopologyService : ISparkplugTopologyService
{
    private static readonly TimeSpan _rebirthCooldown = TimeSpan.FromSeconds(30);

    private readonly IManagedMqttClient _client;
    private readonly ILogger<SparkplugTopologyService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, SpbGroup> _groups = new();
    private bool _disposed;

    public IReadOnlyDictionary<string, SpbGroup> Groups => _groups;
    public event Action? TopologyChanged;

    public SparkplugTopologyService(IManagedMqttClient client, ILogger<SparkplugTopologyService> logger, TimeProvider? timeProvider = null)
    {
        _client = client;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _client.ApplicationMessageReceivedAsync += OnMessageReceived;
    }

    /// <summary>
    /// Removes a single node from the in-memory topology. Returns <c>false</c> if the
    /// group or node does not exist (no event is raised in that case). If the removed
    /// node was the last one in its group, the group is also removed. On successful
    /// removal, <see cref="TopologyChanged"/> is raised exactly once.
    /// </summary>
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

    /// <summary>
    /// Removes every node whose status is <see cref="SpbNodeStatus.Offline"/> from the
    /// in-memory topology. Online and Unknown nodes are untouched. Groups that become
    /// empty as a result are pruned. On at least one successful removal,
    /// <see cref="TopologyChanged"/> is raised exactly once. Returns the number of
    /// nodes removed.
    /// </summary>
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

    private static Payload? TryParsePayload(ReadOnlyMemory<byte> payloadSegment)
    {
        if (payloadSegment.IsEmpty)
            return null;

        try
        {
            return Payload.Parser.ParseFrom(payloadSegment.ToArray());
        }
        catch
        {
            return null;
        }
    }

    internal static (string Value, string DataType) ExtractMetricValue(Payload.Types.Metric metric)
    {
        var dataType = metric.Datatype switch
        {
            1 => "int8",
            2 => "int16",
            3 => "int32",
            4 => "int64",
            5 => "uint8",
            6 => "uint16",
            7 => "uint32",
            8 => "uint64",
            9 => "float",
            10 => "double",
            11 => "bool",
            12 => "string",
            14 => "datetime",
            15 => "text",
            16 => "uuid",
            17 => "dataset",
            18 => "bytes",
            19 => "file",
            20 => "template",
            _ => "unknown"
        };

        var value = metric.ValueCase switch
        {
            Payload.Types.Metric.ValueOneofCase.IntValue => metric.IntValue.ToString(),
            Payload.Types.Metric.ValueOneofCase.LongValue => metric.LongValue.ToString(),
            Payload.Types.Metric.ValueOneofCase.FloatValue => metric.FloatValue.ToString("F4"),
            Payload.Types.Metric.ValueOneofCase.DoubleValue => metric.DoubleValue.ToString("F4"),
            Payload.Types.Metric.ValueOneofCase.BooleanValue => metric.BooleanValue ? "true" : "false",
            Payload.Types.Metric.ValueOneofCase.StringValue => metric.StringValue,
            _ => "—"
        };

        return (value, dataType);
    }

    private SpbNode GetOrCreateNode(string groupId, string nodeId)
    {
        var group = _groups.GetOrAdd(groupId, id => new SpbGroup { GroupId = id });
        return group.Nodes.GetOrAdd(nodeId, id => new SpbNode { NodeId = id, GroupId = groupId });
    }

    private static SpbMetricSnapshot CreateMetricSnapshot(Payload.Types.Metric metric, string name)
    {
        var (value, dataType) = ExtractMetricValue(metric);
        return new SpbMetricSnapshot(name, dataType, value, DateTime.UtcNow);
    }

    private void HandleNBirth(string groupId, string nodeId, Payload payload)
    {
        var node = GetOrCreateNode(groupId, nodeId);
        lock (node.SyncRoot)
        {
            node.Status = SpbNodeStatus.Online;
            node.LastBirthAt = DateTime.UtcNow;
            node.AliasMap.Clear();

            var metrics = new List<SpbMetricSnapshot>();
            foreach (var metric in payload.Metrics)
            {
                if (string.IsNullOrEmpty(metric.Name))
                    continue;

                metrics.Add(CreateMetricSnapshot(metric, metric.Name));
                if (metric.Alias != 0)
                    node.AliasMap[metric.Alias] = metric.Name;
            }

            node.Metrics = metrics.ToArray();
        }

        TopologyChanged?.Invoke();
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

    private void HandleNData(string groupId, string nodeId, Payload payload)
    {
        var node = GetOrCreateNode(groupId, nodeId);
        lock (node.SyncRoot)
        {
            node.LastDataAt = DateTime.UtcNow;
            var metrics = node.Metrics.ToList();

            foreach (var metric in payload.Metrics)
            {
                var name = !string.IsNullOrEmpty(metric.Name)
                    ? metric.Name
                    : node.AliasMap.GetValueOrDefault(metric.Alias);

                if (name == null)
                    continue;

                var idx = metrics.FindIndex(m => m.Name == name);
                var snapshot = CreateMetricSnapshot(metric, name);

                if (idx >= 0)
                    metrics[idx] = snapshot;
                else
                    metrics.Add(snapshot);
            }

            node.Metrics = metrics.ToArray();
        }

        TopologyChanged?.Invoke();
    }

    private void HandleDBirth(string groupId, string nodeId, string deviceId, Payload payload)
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

            var metrics = new List<SpbMetricSnapshot>();
            foreach (var metric in payload.Metrics)
            {
                if (string.IsNullOrEmpty(metric.Name))
                    continue;

                metrics.Add(CreateMetricSnapshot(metric, metric.Name));
                if (metric.Alias != 0)
                    device.AliasMap[metric.Alias] = metric.Name;
            }

            device.Metrics = metrics.ToArray();
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

    private void HandleDData(string groupId, string nodeId, string deviceId, Payload payload)
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

            foreach (var metric in payload.Metrics)
            {
                var name = !string.IsNullOrEmpty(metric.Name)
                    ? metric.Name
                    : device.AliasMap.GetValueOrDefault(metric.Alias);

                if (name == null)
                    continue;

                var idx = metrics.FindIndex(m => m.Name == name);
                var snapshot = CreateMetricSnapshot(metric, name);

                if (idx >= 0)
                    metrics[idx] = snapshot;
                else
                    metrics.Add(snapshot);
            }

            device.Metrics = metrics.ToArray();
        }

        TopologyChanged?.Invoke();
    }

    public async Task RequestNodeRebirthAsync(string groupId, string nodeId)
    {
        if (!_groups.TryGetValue(groupId, out var group))
            return;
        if (!group.Nodes.TryGetValue(nodeId, out var node))
            return;

        lock (node.SyncRoot)
        {
            node.LastRebirthRequestAt = _timeProvider.GetUtcNow().UtcDateTime;
        }

        await PublishRebirthCommandAsync(groupId, nodeId);
    }

    private async Task RequestNodeRebirthIfNeededAsync(string groupId, string nodeId)
    {
        if (!_groups.TryGetValue(groupId, out var group))
            return;
        if (!group.Nodes.TryGetValue(nodeId, out var node))
            return;

        lock (node.SyncRoot)
        {
            if (node.Status == SpbNodeStatus.Online)
                return;

            var now = _timeProvider.GetUtcNow().UtcDateTime;
            if (node.LastRebirthRequestAt != null && now - node.LastRebirthRequestAt < _rebirthCooldown)
                return;

            node.LastRebirthRequestAt = now;
        }

        await PublishRebirthCommandAsync(groupId, nodeId);
    }

    private async Task PublishRebirthCommandAsync(string groupId, string nodeId)
    {
        var payload = new Payload
        {
            Timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        payload.Metrics.Add(new Payload.Types.Metric
        {
            Name = "Node Control/Rebirth",
            Datatype = 11,
            BooleanValue = true
        });

        var topic = $"spBv1.0/{groupId}/NCMD/{nodeId}";
        var managedMessage = new ManagedMqttApplicationMessageBuilder()
            .WithApplicationMessage(new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload.ToByteArray())
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build())
            .Build();

        try
        {
            await _client.EnqueueAsync(managedMessage);
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Requested rebirth for node {GroupId}/{NodeId}", groupId, nodeId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to request rebirth for node {GroupId}/{NodeId}", groupId, nodeId);
        }
    }

    private async Task OnMessageReceived(MqttApplicationMessageReceivedEventArgs arg)
    {
        if (!TryParseTopic(arg.ApplicationMessage.Topic, out var group, out var verb, out var node, out var device))
            return;

        try
        {
            var payload = TryParsePayload(arg.ApplicationMessage.PayloadSegment);

            switch (verb)
            {
                case "NBIRTH" when payload != null:
                    HandleNBirth(group, node, payload);
                    break;
                case "NDEATH":
                    HandleNDeath(group, node);
                    break;
                case "NDATA" when payload != null:
                    HandleNData(group, node, payload);
                    break;
                case "DBIRTH" when payload != null && device != null:
                    HandleDBirth(group, node, device, payload);
                    break;
                case "DDEATH" when device != null:
                    HandleDDeath(group, node, device);
                    break;
                case "DDATA" when payload != null && device != null:
                    HandleDData(group, node, device, payload);
                    break;
            }

            if (verb is "NDATA" or "DDATA")
                await RequestNodeRebirthIfNeededAsync(group, node);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Sparkplug B message on topic {Topic}", arg.ApplicationMessage.Topic);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _client.ApplicationMessageReceivedAsync -= OnMessageReceived;
        _disposed = true;
    }
}
