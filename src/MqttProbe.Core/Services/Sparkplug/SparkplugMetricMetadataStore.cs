using System.Collections.Concurrent;
using Org.Eclipse.Tahu.Protobuf;

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

    public void Record(string groupId, string nodeId, string? deviceId, Payload payload)
    {
        var scope = new Scope();
        foreach (var metric in payload.Metrics)
        {
            if (string.IsNullOrEmpty(metric.Name))
                continue;

            if (metric.Alias != 0)
            {
                scope.AliasNames[metric.Alias] = metric.Name;
                scope.DatatypesByAlias[metric.Alias] = metric.Datatype;
            }

            scope.DatatypesByName[metric.Name] = metric.Datatype;
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
