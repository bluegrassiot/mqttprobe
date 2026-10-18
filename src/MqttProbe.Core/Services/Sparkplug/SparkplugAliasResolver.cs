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
}
