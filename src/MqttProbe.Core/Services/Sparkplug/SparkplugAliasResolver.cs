using System.Collections.Immutable;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Models.Sparkplug;
using MqttProbe.PluginContracts;
using Org.Eclipse.Tahu.Protobuf;

namespace MqttProbe.Core.Services.Sparkplug;

public static class SparkplugAliasResolver
{
    // A plugin decoder may emit valid wire bytes while TypedPayload is null or a foreign type,
    // so the raw payload is the fallback source.
    public static IReadOnlyDictionary<ulong, string>? Resolve(
        string topic,
        DecodedPayloadEnvelope envelope,
        IReadOnlyDictionary<string, SpbGroup> groups) =>
        envelope.TypedPayload is Payload parsed
            ? Resolve(topic, parsed, groups)
            : Resolve(topic, envelope.RawPayload, groups);

    public static IReadOnlyDictionary<ulong, string>? Resolve(
        string topic,
        byte[] rawPayload,
        IReadOnlyDictionary<string, SpbGroup> groups)
    {
        Payload? parsed;
        try
        {
            parsed = Payload.Parser.ParseFrom(rawPayload);
        }
        catch
        {
            return null;
        }

        return Resolve(topic, parsed, groups);
    }

    // Callers that already hold the decoded payload avoid reparsing the protobuf here.
    public static IReadOnlyDictionary<ulong, string>? Resolve(
        string topic,
        Payload? parsed,
        IReadOnlyDictionary<string, SpbGroup> groups)
    {
        if (parsed is null)
            return null;

        if (!SparkplugTopologyService.TryParseTopic(
                topic, out var group, out _, out var node, out var device))
            return null;

        if (!groups.TryGetValue(group, out var grp)
            || !grp.Nodes.TryGetValue(node, out var nodeObj))
            return null;

        var result = new Dictionary<ulong, string>();

        if (device is not null && nodeObj.Devices.TryGetValue(device, out var devObj))
        {
            lock (devObj.SyncRoot)
                CollectAliasNames(parsed, devObj.AliasMap, result);
        }
        else
        {
            lock (nodeObj.SyncRoot)
                CollectAliasNames(parsed, nodeObj.AliasMap, result);
        }

        return result.Count > 0 ? result : null;
    }

    private static void CollectAliasNames(
        Payload parsed,
        Dictionary<ulong, string> aliasMap,
        Dictionary<ulong, string> result)
    {
        foreach (var metric in parsed.Metrics)
        {
            if (metric.Alias != 0
                && string.IsNullOrEmpty(metric.Name)
                && aliasMap.TryGetValue(metric.Alias, out var resolvedName))
            {
                result[metric.Alias] = resolvedName;
            }
        }
    }

    // Metric-name capture is independent of the display enrichment setting and does not report
    // aliases for devices missing from topology: those names are simply unknown.
    public static ImmutableArray<string> ResolveMetricNames(
        string topic,
        DecodedPayloadEnvelope envelope,
        IReadOnlyDictionary<string, SpbGroup>? groups)
    {
        if (envelope.FormatId != "sparkplug-b" || envelope.IsFailure)
            return ImmutableArray<string>.Empty;

        if (!TopicBranchMatcher.TryParseSparkplug(topic, out var path)
            || string.Equals(path.Verb, "STATE", StringComparison.Ordinal))
            return ImmutableArray<string>.Empty;

        var parsed = ParseMetricPayload(envelope);
        if (parsed is null)
            return ImmutableArray<string>.Empty;

        var groupId = path.GroupPath[(SparkplugTopicClassifier.Namespace.Length + 1)..];
        if (groups is null
            || !groups.TryGetValue(groupId, out var group)
            || !group.Nodes.TryGetValue(path.NodeId, out var node))
            return CollectMetricNames(parsed, null);

        if (path.DeviceId is { } deviceId)
        {
            if (!node.Devices.TryGetValue(deviceId, out var device))
                return CollectMetricNames(parsed, null);

            lock (device.SyncRoot)
                return CollectMetricNames(parsed, device.AliasMap);
        }

        lock (node.SyncRoot)
            return CollectMetricNames(parsed, node.AliasMap);
    }

    private static Payload? ParseMetricPayload(DecodedPayloadEnvelope envelope)
    {
        if (envelope.TypedPayload is Payload typed)
            return typed;

        try
        {
            return Payload.Parser.ParseFrom(envelope.RawPayload);
        }
        catch
        {
            return null;
        }
    }

    private static ImmutableArray<string> CollectMetricNames(
        Payload parsed, Dictionary<ulong, string>? aliasMap)
    {
        if (parsed.Metrics.Count == 0)
            return ImmutableArray<string>.Empty;

        var names = ImmutableArray.CreateBuilder<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var metric in parsed.Metrics)
        {
            string? name = null;
            if (!string.IsNullOrEmpty(metric.Name))
                name = metric.Name;
            else if (metric.Alias != 0
                     && aliasMap is not null
                     && aliasMap.TryGetValue(metric.Alias, out var resolvedName))
                name = resolvedName;

            if (name is not null && seen.Add(name))
                names.Add(name);
        }

        return names.ToImmutable();
    }
}
