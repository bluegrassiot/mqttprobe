using System.Collections.Concurrent;
using MqttProbe.PluginContracts;

namespace MqttProbe.Core.Services.Sparkplug;

public sealed class SparkplugMetricMetadataStore
{
    private sealed class Scope
    {
        public Dictionary<ulong, string> AliasNames { get; } = new();
        public Dictionary<ulong, uint> DatatypesByAlias { get; } = new();
        public Dictionary<string, uint> DatatypesByName { get; } = new(StringComparer.Ordinal);
    }

    private readonly ConcurrentDictionary<string, Scope> _scopes = new(StringComparer.Ordinal);

    public void Record(string groupId, string nodeId, string? deviceId, IReadOnlyList<MetricSnapshot> metrics)
    {
        var scope = new Scope();
        foreach (var metric in metrics)
        {
            if (string.IsNullOrEmpty(metric.Name))
                continue;

            var datatype = SparkplugMetricValueConverter.DatatypeForLabel(metric.DataType) ?? 0;
            if (metric.Alias is { } alias && alias != 0)
            {
                scope.AliasNames[alias] = metric.Name;
                scope.DatatypesByAlias[alias] = datatype;
            }

            scope.DatatypesByName[metric.Name] = datatype;
        }

        _scopes[ScopeKey(groupId, nodeId, deviceId)] = scope;
    }

    public string? ResolveName(string groupId, string nodeId, string? deviceId, ulong alias) =>
        _scopes.TryGetValue(ScopeKey(groupId, nodeId, deviceId), out var scope)
            ? scope.AliasNames.GetValueOrDefault(alias)
            : null;

    public uint? ResolveDatatype(string groupId, string nodeId, string? deviceId, ulong alias, string name)
    {
        if (!_scopes.TryGetValue(ScopeKey(groupId, nodeId, deviceId), out var scope))
            return null;

        if (alias != 0 && scope.DatatypesByAlias.TryGetValue(alias, out var byAlias))
            return byAlias;

        return scope.DatatypesByName.TryGetValue(name, out var byName) ? byName : null;
    }

    public void ClearAll() => _scopes.Clear();

    public void RemoveNode(string groupId, string nodeId)
    {
        var prefix = $"{groupId}/{nodeId}";
        _scopes.TryRemove(prefix, out _);
        foreach (var key in _scopes.Keys.Where(
                     key => key.StartsWith(prefix + "/", StringComparison.Ordinal)).ToList())
        {
            _scopes.TryRemove(key, out _);
        }
    }

    public void RemoveDevice(string groupId, string nodeId, string deviceId) =>
        _scopes.TryRemove(ScopeKey(groupId, nodeId, deviceId), out _);

    private static string ScopeKey(string groupId, string nodeId, string? deviceId) =>
        deviceId is null ? $"{groupId}/{nodeId}" : $"{groupId}/{nodeId}/{deviceId}";
}
