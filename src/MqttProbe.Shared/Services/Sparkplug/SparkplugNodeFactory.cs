using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SparkplugNet.Core;
using SparkplugNet.Core.Enumerations;
using SparkplugNet.Core.Node;
using SparkplugNet.VersionB;
using SparkplugNet.VersionB.Data;

namespace MqttProbe.Services.Sparkplug;

public interface ISparkplugNode
{
    public bool IsConnected { get; }
    public event Func<SparkplugBase<Metric>.SparkplugEventArgs, Task>? Connected;
    public event Func<SparkplugBase<Metric>.SparkplugEventArgs, Task>? Disconnected;
    public Task Start(SparkplugNodeOptions options);
    public Task Stop();
    public Task PublishMetrics(List<Metric> metrics);
    public Task PublishNodeDeathMessage();
    public Task PublishDeviceBirthMessage(string deviceId, List<Metric> metrics);
    public Task PublishDeviceMetrics(string deviceId, List<Metric> metrics);
}

public interface ISparkplugNodeFactory
{
    public ISparkplugNode Create(List<Metric> knownMetrics, SparkplugSpecificationVersion version);
    public ISparkplugNode Create(List<Metric> knownMetrics, SparkplugSpecificationVersion version,
        IReadOnlyList<string>? deviceIds, Func<string, List<Metric>>? getDeviceBirthMetrics);
}

/// <summary>
/// A KnownMetricStorage subclass that also tracks metrics by alias, allowing
/// alias-only metrics in NDATA/DDATA to survive FilterMetrics.
/// SparkplugNet's AddVersionBMetric stores name+alias metrics only in knownMetricsByName,
/// leaving knownMetricsByAlias empty. ShouldVersionBMetricBeAdded then rejects alias-only
/// data metrics because the alias lookup fails. This subclass overrides FilterMetrics to
/// accept alias-only data metrics whose alias+datatype match a birth-registered metric.
/// </summary>
internal sealed class AliasAwareKnownMetricStorage : SparkplugBase<Metric>.KnownMetricStorage
{
    private readonly ConcurrentDictionary<ulong, DataType> _knownAliases = new();

    public AliasAwareKnownMetricStorage(IEnumerable<Metric> knownMetrics,
        ILogger<SparkplugBase<Metric>.KnownMetricStorage>? logger = null)
        : base(knownMetrics, logger)
    {
        foreach (var metric in knownMetrics)
        {
            if (metric.Alias.HasValue && metric.Alias.Value != 0)
                _knownAliases[metric.Alias.Value] = metric.DataType;
        }
    }

    public override IEnumerable<Metric> FilterMetrics(
        IEnumerable<Metric> metrics, SparkplugMessageType sparkplugMessageType)
    {
        var isBirth = sparkplugMessageType is SparkplugMessageType.NodeBirth
            or SparkplugMessageType.DeviceBirth;

        var aliasOnlyMetrics = new List<Metric>();
        var otherMetrics = new List<Metric>();

        foreach (var metric in metrics)
        {
            if (!isBirth && string.IsNullOrWhiteSpace(metric.Name) && metric.Alias.HasValue)
                aliasOnlyMetrics.Add(metric);
            else
                otherMetrics.Add(metric);
        }

        var result = new List<Metric>(base.FilterMetrics(otherMetrics, sparkplugMessageType));

        // Accept alias-only data metrics that match a known alias + datatype.
        // Unknown aliases or datatype mismatches are silently dropped (no arbitrary aliases).
        foreach (var metric in aliasOnlyMetrics)
        {
            if (_knownAliases.TryGetValue(metric.Alias!.Value, out var expectedType)
                && metric.DataType == expectedType)
            {
                result.Add(metric);
            }
        }

        return result;
    }
}

internal sealed class SparkplugNodeAdapter : ISparkplugNode
{
    private readonly SparkplugNode _node;
    private readonly ILogger? _logger;
    private readonly List<Metric> _knownMetrics;
    private readonly IReadOnlyList<string>? _deviceIds;
    private readonly Func<string, List<Metric>>? _getDeviceBirthMetrics;

    public SparkplugNodeAdapter(SparkplugNode node, List<Metric> knownMetrics,
        IReadOnlyList<string>? deviceIds = null,
        Func<string, List<Metric>>? getDeviceBirthMetrics = null,
        ILogger? logger = null)
    {
        _node = node;
        _knownMetrics = knownMetrics;
        _deviceIds = deviceIds;
        _getDeviceBirthMetrics = getDeviceBirthMetrics;
        _logger = logger;
        _node.NodeCommandReceived += OnNodeCommandReceived;
    }

    private async Task OnNodeCommandReceived(SparkplugNodeBase<Metric>.NodeCommandEventArgs args)
    {
        var hasRebirth = args.Metrics.Any(m =>
            m.Name == "Node Control/Rebirth"
            && m.Value is bool b && b);
        if (hasRebirth)
        {
            if (_logger?.IsEnabled(LogLevel.Information) == true)
                _logger.LogInformation("Rebirth command received for node {GroupId}/{NodeId}",
                    args.GroupIdentifier, args.EdgeNodeIdentifier);

            if (!_node.IsConnected)
            {
                _logger?.LogWarning(
                    "Rebirth command ignored for node {GroupId}/{NodeId}: client is not connected. " +
                    "The broker already published the NDEATH LWT; NBIRTH will be sent on reconnect.",
                    args.GroupIdentifier, args.EdgeNodeIdentifier);
                return;
            }

            try
            {
                await _node.Rebirth(_knownMetrics);
            }
            catch (MQTTnet.Client.MqttClientDisconnectedException ex)
            {
                _logger?.LogWarning(ex,
                    "Rebirth aborted for node {GroupId}/{NodeId}: client disconnected mid-rebirth. " +
                    "NBIRTH will be sent on reconnect.",
                    args.GroupIdentifier, args.EdgeNodeIdentifier);
                return;
            }

            // Rebirth internally replaces knownMetrics with a plain KnownMetricStorage,
            // losing alias tracking. Restore alias-aware storage for subsequent NDATA.
            // NOSONAR — knownMetrics is protected with no public setter; SparkplugNode is sealed.
            var field = typeof(SparkplugBase<Metric>).GetField("knownMetrics",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            field?.SetValue(_node, new AliasAwareKnownMetricStorage(_knownMetrics));

            // Re-publish DBIRTH for each device (SparkplugB spec requirement).
            // SparkplugNet's Rebirth only re-publishes NBIRTH; DBIRTH must be
            // explicitly republished by the application.
            if (_getDeviceBirthMetrics is not null && _deviceIds is not null)
            {
                foreach (var deviceId in _deviceIds)
                {
                    var metrics = _getDeviceBirthMetrics(deviceId);
                    await _node.PublishDeviceBirthMessage(metrics, deviceId);
                }
            }
        }
    }

    public bool IsConnected => _node.IsConnected;

    public event Func<SparkplugBase<Metric>.SparkplugEventArgs, Task>? Connected
    {
        add => _node.Connected += value;
        remove => _node.Connected -= value;
    }

    public event Func<SparkplugBase<Metric>.SparkplugEventArgs, Task>? Disconnected
    {
        add => _node.Disconnected += value;
        remove => _node.Disconnected -= value;
    }

    public Task Start(SparkplugNodeOptions options) => _node.Start(options);
    public Task Stop() => _node.Stop();
    public Task PublishMetrics(List<Metric> metrics) => _node.PublishMetrics(metrics);

    public Task PublishNodeDeathMessage()
    {
        System.Reflection.MethodInfo? method = null;
        for (var type = _node.GetType(); type is not null; type = type.BaseType)
        {
            method = type.GetMethod(
                "SendNodeDeathMessage",
                System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.DeclaredOnly); // NOSONAR — SendNodeDeathMessage is not exposed publicly by SparkplugNet
            if (method is not null)
                break;
        }

        if (method is not null)
            return (Task)method.Invoke(_node, null)!;

        throw new InvalidOperationException(
            "SparkplugNet method 'SendNodeDeathMessage' was not found via reflection. " +
            "The method may have been renamed or removed in a SparkplugNet update. " +
            "Update the reflection call in SparkplugNodeAdapter.PublishNodeDeathMessage.");
    }

    public async Task PublishDeviceBirthMessage(string deviceId, List<Metric> metrics)
    {
        await _node.PublishDeviceBirthMessage(metrics, deviceId);
        // SparkplugNet creates a plain KnownMetricStorage for the device, which
        // lacks alias tracking. Replace with alias-aware version so DDATA
        // alias-only metrics survive FilterMetrics.
        _node.KnownDevices[deviceId] = new AliasAwareKnownMetricStorage(metrics);
    }

    public Task PublishDeviceMetrics(string deviceId, List<Metric> metrics)
        => _node.PublishDeviceData(metrics, deviceId);
}

public class SparkplugNodeFactory(ILogger<SparkplugNodeFactory>? logger = null) : ISparkplugNodeFactory
{
    public ISparkplugNode Create(List<Metric> knownMetrics, SparkplugSpecificationVersion version)
    {
        var storage = new AliasAwareKnownMetricStorage(knownMetrics);
        return new SparkplugNodeAdapter(new SparkplugNode(storage, version), knownMetrics, logger: logger);
    }

    public ISparkplugNode Create(List<Metric> knownMetrics, SparkplugSpecificationVersion version,
        IReadOnlyList<string>? deviceIds, Func<string, List<Metric>>? getDeviceBirthMetrics)
    {
        var storage = new AliasAwareKnownMetricStorage(knownMetrics);
        return new SparkplugNodeAdapter(new SparkplugNode(storage, version), knownMetrics,
            deviceIds, getDeviceBirthMetrics, logger);
    }
}
