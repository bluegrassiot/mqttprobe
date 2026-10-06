using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SparkplugNet.Core;
using SparkplugNet.Core.Enumerations;
using SparkplugNet.Core.Node;
using SparkplugNet.VersionB;
using SparkplugNet.VersionB.Data;

namespace MqttProbe.Core.Services.Sparkplug;

public interface ISparkplugNode
{
    public bool IsConnected { get; }
    public event Func<SparkplugBase<Metric>.SparkplugEventArgs, Task>? Connected;
    public event Func<SparkplugBase<Metric>.SparkplugEventArgs, Task>? Disconnected;
    public Task Start(SparkplugNodeOptions options);
    // CA1716: "Stop" matches a VB keyword. Plugin-facing public API — do not rename.
#pragma warning disable CA1716
    public Task Stop();
#pragma warning restore CA1716
    public Task PublishMetrics(IReadOnlyList<Metric> metrics);
    public Task PublishNodeDeathMessage();
    public Task PublishDeviceBirthMessage(string deviceId, IReadOnlyList<Metric> metrics);
    public Task PublishDeviceMetrics(string deviceId, IReadOnlyList<Metric> metrics);
}

public interface ISparkplugNodeFactory
{
    public ISparkplugNode Create(IReadOnlyList<Metric> knownMetrics, SparkplugSpecificationVersion version,
        CancellationToken cancellationToken = default);
    public ISparkplugNode Create(IReadOnlyList<Metric> knownMetrics, SparkplugSpecificationVersion version,
        IReadOnlyList<string>? deviceIds, Func<string, IReadOnlyList<Metric>>? getDeviceBirthMetrics,
        CancellationToken cancellationToken = default);
}

// Tracks metrics by alias so alias-only NDATA/DDATA metrics survive FilterMetrics.
// SparkplugNet's AddVersionBMetric stores name+alias metrics only in knownMetricsByName,
// leaving knownMetricsByAlias empty. ShouldVersionBMetricBeAdded then rejects alias-only
// data metrics because the alias lookup fails. FilterMetrics is overridden here to accept
// alias-only data metrics whose alias+datatype match a birth-registered metric.
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
    private readonly IReadOnlyList<Metric> _knownMetrics;
    private readonly IReadOnlyList<string>? _deviceIds;
    private readonly Func<string, IReadOnlyList<Metric>>? _getDeviceBirthMetrics;
    private readonly CancellationToken _revocationToken;

    public SparkplugNodeAdapter(SparkplugNode node, IReadOnlyList<Metric> knownMetrics,
        IReadOnlyList<string>? deviceIds = null,
        Func<string, IReadOnlyList<Metric>>? getDeviceBirthMetrics = null,
        ILogger? logger = null,
        CancellationToken revocationToken = default)
    {
        _node = node;
        _knownMetrics = knownMetrics;
        _deviceIds = deviceIds;
        _getDeviceBirthMetrics = getDeviceBirthMetrics;
        _logger = logger;
        _revocationToken = revocationToken;
        _node.NodeCommandReceived += OnNodeCommandReceived;
    }

    private async Task OnNodeCommandReceived(SparkplugNodeBase<Metric>.NodeCommandEventArgs args)
    {
        var hasRebirth = args.Metrics.Any(m =>
            m.Name == "Node Control/Rebirth" && m.Value is bool b && b);
        if (!hasRebirth) return;

        if (_revocationToken.IsCancellationRequested)
        {
            _logger?.LogWarning("Rebirth ignored for {GroupId}/{NodeId}: gate revoked",
                args.GroupIdentifier, args.EdgeNodeIdentifier);
            return;
        }

        if (_logger?.IsEnabled(LogLevel.Information) == true)
            _logger.LogInformation("Rebirth command received for node {GroupId}/{NodeId}",
                args.GroupIdentifier, args.EdgeNodeIdentifier);

        if (!_node.IsConnected)
        {
            _logger?.LogWarning("Rebirth ignored for {GroupId}/{NodeId}: not connected",
                args.GroupIdentifier, args.EdgeNodeIdentifier);
            return;
        }

        await ExecuteRebirthAsync(args).ConfigureAwait(false);
    }

    private async Task ExecuteRebirthAsync(SparkplugNodeBase<Metric>.NodeCommandEventArgs args)
    {
        try
        {
            await _node.Rebirth(_knownMetrics).ConfigureAwait(false);
        }
        catch (MQTTnet.Exceptions.MqttClientDisconnectedException ex)
        {
            _logger?.LogWarning(ex, "Rebirth aborted for {GroupId}/{NodeId}: disconnected",
                args.GroupIdentifier, args.EdgeNodeIdentifier);
            return;
        }

        if (_revocationToken.IsCancellationRequested)
        {
            _logger?.LogWarning("Rebirth completed but device births skipped for {GroupId}/{NodeId}: gate revoked",
                args.GroupIdentifier, args.EdgeNodeIdentifier);
            return;
        }

        RestoreAliasAwareStorage();
        await RepublishDeviceBirthsAsync().ConfigureAwait(false);
    }

    private void RestoreAliasAwareStorage()
    {
        var field = typeof(SparkplugBase<Metric>).GetField("knownMetrics",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field?.SetValue(_node, new AliasAwareKnownMetricStorage(_knownMetrics));
    }

    private async Task RepublishDeviceBirthsAsync()
    {
        if (_getDeviceBirthMetrics is null || _deviceIds is null) return;

        foreach (var deviceId in _deviceIds)
        {
            if (_revocationToken.IsCancellationRequested)
            {
                _logger?.LogWarning("Device birth skipped for {DeviceId}: gate revoked", deviceId);
                return;
            }

            var metrics = _getDeviceBirthMetrics(deviceId);
            await _node.PublishDeviceBirthMessage(metrics.ToList(), deviceId).ConfigureAwait(false);
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

    public Task PublishMetrics(IReadOnlyList<Metric> metrics)
    {
        if (_revocationToken.IsCancellationRequested)
            return Task.CompletedTask;
        return _node.PublishMetrics(metrics);
    }

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

    public async Task PublishDeviceBirthMessage(string deviceId, IReadOnlyList<Metric> metrics)
    {
        if (_revocationToken.IsCancellationRequested)
            return;
        // SparkplugNet takes IEnumerable<T> everywhere except this one method,
        // which requires a List<T>.
        await _node.PublishDeviceBirthMessage(metrics.ToList(), deviceId);
        // SparkplugNet creates a plain KnownMetricStorage for the device, which
        // lacks alias tracking. Replace with alias-aware version so DDATA
        // alias-only metrics survive FilterMetrics.
        _node.KnownDevices[deviceId] = new AliasAwareKnownMetricStorage(metrics);
    }

    public Task PublishDeviceMetrics(string deviceId, IReadOnlyList<Metric> metrics)
    {
        if (_revocationToken.IsCancellationRequested)
            return Task.CompletedTask;
        return _node.PublishDeviceData(metrics, deviceId);
    }
}

public class SparkplugNodeFactory(ILogger<SparkplugNodeFactory>? logger = null) : ISparkplugNodeFactory
{
    public ISparkplugNode Create(IReadOnlyList<Metric> knownMetrics, SparkplugSpecificationVersion version,
        CancellationToken cancellationToken = default)
    {
        var storage = new AliasAwareKnownMetricStorage(knownMetrics);
        return new SparkplugNodeAdapter(new SparkplugNode(storage, version), knownMetrics,
            logger: logger, revocationToken: cancellationToken);
    }

    public ISparkplugNode Create(IReadOnlyList<Metric> knownMetrics, SparkplugSpecificationVersion version,
        IReadOnlyList<string>? deviceIds, Func<string, IReadOnlyList<Metric>>? getDeviceBirthMetrics,
        CancellationToken cancellationToken = default)
    {
        var storage = new AliasAwareKnownMetricStorage(knownMetrics);
        return new SparkplugNodeAdapter(new SparkplugNode(storage, version), knownMetrics,
            deviceIds, getDeviceBirthMetrics, logger, cancellationToken);
    }
}
