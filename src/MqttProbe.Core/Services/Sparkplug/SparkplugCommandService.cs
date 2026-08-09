using Google.Protobuf;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Protocol;
using MqttProbe.Core.Models.Sparkplug;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Mqtt;
using Org.Eclipse.Tahu.Protobuf;

namespace MqttProbe.Core.Services.Sparkplug;

public enum SparkplugCommandResult
{
    Published,
    TargetNotFound,
    NotAllowed,
    Failed
}

public interface ISparkplugCommandService
{
    public Task<SparkplugCommandResult> RequestNodeRebirthAsync(string groupId, string nodeId);
    public Task<SparkplugCommandResult> RequestDeviceRebirthAsync(string groupId, string nodeId, string deviceId);
    public Task<SparkplugCommandResult> RequestNodeRebootAsync(string groupId, string nodeId);
    public Task RequestNodeRebirthIfNeededAsync(string groupId, string nodeId);
}

public sealed class SparkplugCommandService(
    IMqttManagedClient client,
    ILogger<SparkplugCommandService> logger,
    ISparkplugSettings sparkplugSettings,
    ISparkplugTopologyService topologyService,
    TimeProvider? timeProvider = null) : ISparkplugCommandService
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<SparkplugCommandResult> RequestNodeRebirthAsync(string groupId, string nodeId)
    {
        if (!topologyService.Groups.TryGetValue(groupId, out var group))
            return SparkplugCommandResult.TargetNotFound;
        if (!group.Nodes.TryGetValue(nodeId, out var node))
            return SparkplugCommandResult.TargetNotFound;

        lock (node.SyncRoot)
        {
            node.LastRebirthRequestAt = _timeProvider.GetUtcNow().UtcDateTime;
        }

        return await PublishNcmdAsync(groupId, nodeId, "Node Control/Rebirth", "rebirth");
    }

    public async Task<SparkplugCommandResult> RequestDeviceRebirthAsync(string groupId, string nodeId, string deviceId)
    {
        if (!topologyService.Groups.TryGetValue(groupId, out var group))
            return SparkplugCommandResult.TargetNotFound;
        if (!group.Nodes.TryGetValue(nodeId, out var node))
            return SparkplugCommandResult.TargetNotFound;
        if (!node.Devices.ContainsKey(deviceId))
            return SparkplugCommandResult.TargetNotFound;

        return await PublishDcmdAsync(groupId, nodeId, deviceId, "Device Control/Rebirth", "device rebirth");
    }

    public async Task<SparkplugCommandResult> RequestNodeRebootAsync(string groupId, string nodeId)
    {
        if (!sparkplugSettings.Sparkplug.AllowNodeReboot)
            return SparkplugCommandResult.NotAllowed;

        if (!topologyService.Groups.TryGetValue(groupId, out var group))
            return SparkplugCommandResult.TargetNotFound;
        if (!group.Nodes.TryGetValue(nodeId, out _))
            return SparkplugCommandResult.TargetNotFound;

        return await PublishNcmdAsync(groupId, nodeId, "Node Control/Reboot", "node reboot");
    }

    public async Task RequestNodeRebirthIfNeededAsync(string groupId, string nodeId)
    {
        if (!sparkplugSettings.Sparkplug.AutoRequestRebirth)
            return;

        if (!topologyService.Groups.TryGetValue(groupId, out var group))
            return;
        if (!group.Nodes.TryGetValue(nodeId, out var node))
            return;

        var cooldown = TimeSpan.FromSeconds(sparkplugSettings.Sparkplug.RebirthCooldownSeconds);

        lock (node.SyncRoot)
        {
            if (node.Status == SpbNodeStatus.Online)
                return;

            var now = _timeProvider.GetUtcNow().UtcDateTime;
            if (node.LastRebirthRequestAt != null && now - node.LastRebirthRequestAt < cooldown)
                return;

            node.LastRebirthRequestAt = now;
        }

        await PublishNcmdAsync(groupId, nodeId, "Node Control/Rebirth", "rebirth");
    }

    private async Task<SparkplugCommandResult> PublishNcmdAsync(string groupId, string nodeId, string metricName, string label)
    {
        var payload = new Payload
        {
            Timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        payload.Metrics.Add(new Payload.Types.Metric
        {
            Name = metricName,
            Datatype = 11,
            BooleanValue = true
        });

        var topic = $"spBv1.0/{groupId}/NCMD/{nodeId}";
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload.ToByteArray())
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        try
        {
            await client.EnqueueAsync(message);
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation("Requested {Label} for node {GroupId}/{NodeId}", label, groupId, nodeId);
            return SparkplugCommandResult.Published;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to request {Label} for node {GroupId}/{NodeId}", label, groupId, nodeId);
            return SparkplugCommandResult.Failed;
        }
    }

    private async Task<SparkplugCommandResult> PublishDcmdAsync(
        string groupId, string nodeId, string deviceId, string metricName, string label)
    {
        var payload = new Payload
        {
            Timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        payload.Metrics.Add(new Payload.Types.Metric
        {
            Name = metricName,
            Datatype = 11,
            BooleanValue = true
        });

        var topic = $"spBv1.0/{groupId}/DCMD/{nodeId}/{deviceId}";
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload.ToByteArray())
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        try
        {
            await client.EnqueueAsync(message);
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation("Requested {Label} for device {GroupId}/{NodeId}/{DeviceId}", label, groupId, nodeId, deviceId);
            return SparkplugCommandResult.Published;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to request {Label} for device {GroupId}/{NodeId}/{DeviceId}", label, groupId, nodeId, deviceId);
            return SparkplugCommandResult.Failed;
        }
    }
}
