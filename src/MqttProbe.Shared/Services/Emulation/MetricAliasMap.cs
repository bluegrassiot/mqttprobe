using MqttProbe.Models.Emulation;
using SparkplugNet.VersionB.Data;

namespace MqttProbe.Services.Emulation;

// Aliases are assigned once at birth and reused for every later message, so the maps
// must be built from the exact metric lists that the birth messages carry.
internal sealed class MetricAliasMap
{
    private readonly Dictionary<string, ulong> _nodeAliases;
    private readonly Dictionary<string, Dictionary<string, ulong>> _deviceAliases;

    private MetricAliasMap(
        Dictionary<string, ulong> nodeAliases,
        Dictionary<string, Dictionary<string, ulong>> deviceAliases)
    {
        _nodeAliases = nodeAliases;
        _deviceAliases = deviceAliases;
    }

    public static MetricAliasMap Build(
        IReadOnlyList<Metric> nodeMetrics, IReadOnlyList<EmulatorDeviceConfig> devices)
    {
        var nodeAliases = new Dictionary<string, ulong>(StringComparer.Ordinal);
        ulong alias = 1;
        foreach (var metric in nodeMetrics)
        {
            nodeAliases[metric.Name] = alias++;
        }

        var deviceAliases = new Dictionary<string, Dictionary<string, ulong>>(StringComparer.Ordinal);
        foreach (var device in devices)
        {
            var deviceMap = new Dictionary<string, ulong>(StringComparer.Ordinal);
            ulong deviceAlias = 1;
            foreach (var metric in device.Metrics)
            {
                deviceMap[metric.Name] = deviceAlias++;
            }

            deviceAliases[device.DeviceId] = deviceMap;
        }

        var map = new MetricAliasMap(nodeAliases, deviceAliases);
        map.Validate();
        return map;
    }

    public ulong DeviceAlias(string deviceId, string metricName)
    {
        if (!_deviceAliases.TryGetValue(deviceId, out var deviceMap)
            || !deviceMap.TryGetValue(metricName, out var alias))
            throw new InvalidOperationException(
                $"Missing alias for metric '{metricName}' in device '{deviceId}'. " +
                "Alias maps may be out of sync with config.");

        return alias;
    }

    // Births carry name and alias; data messages carry the alias alone.
    public List<Metric> ApplyToNodeMetrics(IReadOnlyList<Metric> metrics, bool isBirth)
    {
        var result = new List<Metric>(metrics.Count);
        foreach (var source in metrics)
        {
            if (!_nodeAliases.TryGetValue(source.Name, out var alias))
                throw new InvalidOperationException(
                    $"Missing alias for node metric '{source.Name}'. " +
                    "Alias map may be out of sync with config.");

            var m = new Metric(source.Name, source.DataType, source.Value) { Alias = alias };
            if (!isBirth)
                m.Name = null!;
            result.Add(m);
        }

        return result;
    }

    private void Validate()
    {
        RequireUniqueNonZero(_nodeAliases.Values, "Node");
        foreach (var (deviceId, aliases) in _deviceAliases)
        {
            RequireUniqueNonZero(aliases.Values, $"Device '{deviceId}'");
        }
    }

    private static void RequireUniqueNonZero(Dictionary<string, ulong>.ValueCollection aliases, string owner)
    {
        if (aliases.Any(v => v == 0))
            throw new InvalidOperationException(
                $"{owner} alias map contains alias 0, which is reserved. All aliases must be >= 1.");

        if (aliases.Distinct().Count() != aliases.Count)
            throw new InvalidOperationException(
                $"{owner} alias map contains duplicate alias values.");
    }
}
