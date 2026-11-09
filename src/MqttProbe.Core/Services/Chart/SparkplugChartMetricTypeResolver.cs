using MqttProbe.Core.Models.Sparkplug;
using MqttProbe.Core.Services.Sparkplug;

namespace MqttProbe.Core.Services.Chart;

public interface ISparkplugMetricTypeResolver
{
    public uint? Resolve(ulong metricAlias, string? name);
}

public sealed class SparkplugChartMetricTypeResolver(
    string topic,
    ISparkplugTopologyService? topology) : ISparkplugMetricTypeResolver
{
    public uint? Resolve(ulong metricAlias, string? name)
    {
        if (topology is null
            || !SparkplugTopologyService.TryParseTopic(topic, out var group, out _, out var node, out var device)
            || !topology.Groups.TryGetValue(group, out var groupSnapshot)
            || !groupSnapshot.Nodes.TryGetValue(node, out var nodeSnapshot))
            return null;

        IReadOnlyList<SpbMetricSnapshot> metrics;
        if (device is not null)
        {
            if (!nodeSnapshot.Devices.TryGetValue(device, out var deviceSnapshot))
                return null;

            metrics = deviceSnapshot.Metrics;
        }
        else
        {
            metrics = nodeSnapshot.Metrics;
        }

        if (metricAlias != 0
            && metrics.FirstOrDefault(metric => metric.Alias == metricAlias) is { } byAlias
            && MapDataType(byAlias.DataType) is { } aliasType)
        {
            return aliasType;
        }

        return name is not null
            && metrics.FirstOrDefault(metric => string.Equals(metric.Name, name, StringComparison.Ordinal)) is { } byName
            ? MapDataType(byName.DataType)
            : null;
    }

    private static uint? MapDataType(string dataType) => dataType switch
    {
        "int8" => 1,
        "int16" => 2,
        "int32" => 3,
        "int64" => 4,
        "uint8" => 5,
        "uint16" => 6,
        "uint32" => 7,
        "uint64" => 8,
        _ => null
    };
}
