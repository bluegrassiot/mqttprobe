using MqttProbe.Models.Sparkplug;
using Org.Eclipse.Tahu.Protobuf;

namespace MqttProbe.Services.Sparkplug;

public static class SparkplugAliasResolver
{
    public static IReadOnlyDictionary<ulong, string>? Resolve(
        string topic,
        byte[] rawPayload,
        IReadOnlyDictionary<string, SpbGroup> groups)
    {
        if (!SparkplugTopologyService.TryParseTopic(
                topic, out var group, out var verb, out var node, out var device))
            return null;

        Payload? parsed;
        try
        {
            parsed = Payload.Parser.ParseFrom(rawPayload);
        }
        catch
        {
            return null;
        }

        if (parsed is null)
            return null;

        Dictionary<ulong, string>? aliasMap = null;

        if (groups.TryGetValue(group, out var grp)
            && grp.Nodes.TryGetValue(node, out var nodeObj))
        {
            if (device is not null
                && nodeObj.Devices.TryGetValue(device, out var devObj))
            {
                lock (devObj.SyncRoot)
                {
                    aliasMap = devObj.AliasMap.Count > 0
                        ? new Dictionary<ulong, string>(devObj.AliasMap)
                        : null;
                }
            }
            else
            {
                lock (nodeObj.SyncRoot)
                {
                    aliasMap = nodeObj.AliasMap.Count > 0
                        ? new Dictionary<ulong, string>(nodeObj.AliasMap)
                        : null;
                }
            }
        }

        if (aliasMap is null)
            return null;

        var result = new Dictionary<ulong, string>();
        foreach (var metric in parsed.Metrics)
        {
            if (metric.Alias != 0
                && string.IsNullOrEmpty(metric.Name)
                && aliasMap.TryGetValue(metric.Alias, out var resolvedName))
            {
                result[metric.Alias] = resolvedName;
            }
        }

        return result.Count > 0 ? result : null;
    }
}
