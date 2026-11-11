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
    public Task RequestNodeRebirthIfNeededAsync(
        string groupId,
        string nodeId,
        long? expectedConnectionEpoch = null,
        CancellationToken cancellationToken = default);

    public Task RequestNodeRebirthIfNeededAsync(
        string groupId,
        string nodeId,
        long? expectedConnectionEpoch,
        long? expectedSubscriptionRevision,
        CancellationToken cancellationToken) => Task.CompletedTask;
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

        return await PublishCommandAsync(
            groupId, nodeId, null, TriggerMetric(SparkplugControlMetrics.NodeRebirth), "rebirth");
    }

    public async Task<SparkplugCommandResult> RequestDeviceRebirthAsync(string groupId, string nodeId, string deviceId)
    {
        if (!topologyService.Groups.TryGetValue(groupId, out var group))
            return SparkplugCommandResult.TargetNotFound;
        if (!group.Nodes.TryGetValue(nodeId, out var node))
            return SparkplugCommandResult.TargetNotFound;
        if (!node.Devices.ContainsKey(deviceId))
            return SparkplugCommandResult.TargetNotFound;

        return await PublishCommandAsync(
            groupId, nodeId, deviceId, TriggerMetric(SparkplugControlMetrics.DeviceRebirth), "device rebirth");
    }

    public async Task<SparkplugCommandResult> RequestNodeRebootAsync(string groupId, string nodeId)
    {
        if (!sparkplugSettings.Sparkplug.AllowNodeReboot)
            return SparkplugCommandResult.NotAllowed;

        if (!topologyService.Groups.TryGetValue(groupId, out var group))
            return SparkplugCommandResult.TargetNotFound;
        if (!group.Nodes.TryGetValue(nodeId, out _))
            return SparkplugCommandResult.TargetNotFound;

        return await PublishCommandAsync(
            groupId, nodeId, null, TriggerMetric(SparkplugControlMetrics.NodeReboot), "node reboot");
    }

    public async Task RequestNodeRebirthIfNeededAsync(
        string groupId,
        string nodeId,
        long? expectedConnectionEpoch = null,
        CancellationToken cancellationToken = default) =>
        await RequestNodeRebirthIfNeededAsync(groupId, nodeId, expectedConnectionEpoch, null, cancellationToken)
            .ConfigureAwait(false);

    public async Task RequestNodeRebirthIfNeededAsync(
        string groupId,
        string nodeId,
        long? expectedConnectionEpoch,
        long? expectedSubscriptionRevision,
        CancellationToken cancellationToken)
    {
        if (!sparkplugSettings.Sparkplug.AutoRequestRebirth)
            return;

        if (!topologyService.Groups.TryGetValue(groupId, out var group))
            return;
        if (!group.Nodes.TryGetValue(nodeId, out var node))
            return;

        var cooldown = TimeSpan.FromSeconds(sparkplugSettings.Sparkplug.RebirthCooldownSeconds);
        if (!TryReserveRebirth(node, cooldown, expectedConnectionEpoch, expectedSubscriptionRevision,
                out var isSequenceFault, out var episodeId))
            return;

        var result = await PublishCommandAsync(
            groupId,
            nodeId,
            null,
            TriggerMetric(SparkplugControlMetrics.NodeRebirth),
            "rebirth",
            isSequenceFault,
            isSequenceFault ? () => IsCurrentSequenceFault(
                groupId, nodeId, node, episodeId, expectedConnectionEpoch, expectedSubscriptionRevision) : null,
            isSequenceFault ? expectedConnectionEpoch : null,
            isSequenceFault ? expectedSubscriptionRevision : null,
            cancellationToken).ConfigureAwait(false);
        if (isSequenceFault)
        {
            lock (node.SyncRoot)
            {
                if (node.SequenceEpisodeId == episodeId
                    && node.SequenceRecoveryInFlightEpisodeId == episodeId)
                {
                    node.SequenceRecoveryInFlightEpisodeId = null;
                    if (result == SparkplugCommandResult.Published
                        && node.Sequence.State == SpbSequenceState.GapDetected)
                        node.SequenceRecoverySentEpisodeId = episodeId;
                }
            }
        }
    }

    private bool TryReserveRebirth(
        SpbNode node,
        TimeSpan cooldown,
        long? expectedConnectionEpoch,
        long? expectedSubscriptionRevision,
        out bool isSequenceFault,
        out long episodeId)
    {
        lock (node.SyncRoot)
        {
            isSequenceFault = node.Status == SpbNodeStatus.Online
                && node.Sequence.State == SpbSequenceState.GapDetected;
            episodeId = isSequenceFault ? node.SequenceEpisodeId : 0;

            if (isSequenceFault)
            {
                if (!node.SequenceObservationComplete
                    || expectedConnectionEpoch is null
                    || node.SequenceConnectionEpoch != expectedConnectionEpoch
                    || expectedSubscriptionRevision is null
                    || node.SequenceSubscriptionRevision != expectedSubscriptionRevision
                    || node.SequenceRecoveryInFlightEpisodeId == episodeId
                    || node.SequenceRecoverySentEpisodeId == episodeId)
                    return false;
            }
            else if (node.Status == SpbNodeStatus.Online)
            {
                return false;
            }

            // Missing-birth recovery must not depend on sequence validity.
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            if (node.LastRebirthRequestAt != null && now - node.LastRebirthRequestAt < cooldown)
                return false;

            node.LastRebirthRequestAt = now;
            if (isSequenceFault)
                node.SequenceRecoveryInFlightEpisodeId = episodeId;
            return true;
        }
    }

    private static Payload.Types.Metric TriggerMetric(string metricName) =>
        new() { Name = metricName, Datatype = 11, BooleanValue = true };

    private async Task<SparkplugCommandResult> PublishCommandAsync(
        string groupId,
        string nodeId,
        string? deviceId,
        Payload.Types.Metric metric,
        string label,
        bool requireConnected = false,
        Func<bool>? canDispatch = null,
        long? expectedConnectionEpoch = null,
        long? expectedSubscriptionRevision = null,
        CancellationToken cancellationToken = default)
    {
        var payload = new Payload
        {
            Timestamp = (ulong)_timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
        };
        payload.Metrics.Add(metric);

        var target = deviceId is null ? $"{groupId}/{nodeId}" : $"{groupId}/{nodeId}/{deviceId}";
        var topic = deviceId is null
            ? $"spBv1.0/{groupId}/NCMD/{nodeId}"
            : $"spBv1.0/{groupId}/DCMD/{nodeId}/{deviceId}";

        // Sparkplug commands are QoS 0, not retained, and carry no seq.
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload.ToByteArray())
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
            .WithRetainFlag(false)
            .Build();

        try
        {
            if (canDispatch is not null && !canDispatch())
                return SparkplugCommandResult.Failed;
            var published = requireConnected
                ? await client.TryPublishConnectedAsync(
                    message, expectedConnectionEpoch, expectedSubscriptionRevision, cancellationToken).ConfigureAwait(false)
                : await PublishQueuedAsync(message, cancellationToken).ConfigureAwait(false);
            if (!published)
                return SparkplugCommandResult.Failed;
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation("Requested {Label} for {Target}", label, target);
            return SparkplugCommandResult.Published;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to request {Label} for {Target}", label, target);
            return SparkplugCommandResult.Failed;
        }
    }

    private bool IsCurrentSequenceFault(
        string groupId,
        string nodeId,
        SpbNode expectedNode,
        long episodeId,
        long? expectedConnectionEpoch,
        long? expectedSubscriptionRevision)
    {
        if (!topologyService.Groups.TryGetValue(groupId, out var group)
            || !group.Nodes.TryGetValue(nodeId, out var currentNode)
            || !ReferenceEquals(expectedNode, currentNode))
            return false;

        lock (currentNode.SyncRoot)
            return currentNode.Sequence.State == SpbSequenceState.GapDetected
                && currentNode.SequenceObservationComplete
                && currentNode.SequenceEpisodeId == episodeId
                && currentNode.SequenceConnectionEpoch == expectedConnectionEpoch
                && currentNode.SequenceSubscriptionRevision == expectedSubscriptionRevision
                && currentNode.SequenceRecoverySentEpisodeId != episodeId;
    }

    private async Task<bool> PublishQueuedAsync(MqttApplicationMessage message, CancellationToken cancellationToken)
    {
        await client.EnqueueAsync(message, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
