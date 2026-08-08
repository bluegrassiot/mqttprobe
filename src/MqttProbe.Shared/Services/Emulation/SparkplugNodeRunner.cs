using Microsoft.Extensions.Logging;
using MqttProbe.Models.Emulation;
using MqttProbe.Models.Mqtt;
using MqttProbe.Services.Security;
using MqttProbe.Services.Sparkplug;
using SparkplugNet.Core.Enumerations;
using SparkplugNet.VersionB.Data;

namespace MqttProbe.Services.Emulation;

public class SparkplugNodeRunner(
    EmulatorNodeConfig config,
    ISparkplugNodeFactory nodeFactory,
    Connection connection,
    IReadOnlyList<Metric> initialKnownMetrics,
    ICertificateAssetStore certStore,
    ICertificateSessionQuarantine quarantine,
    ILogger logger) : INodeRunner
{
    private readonly Dictionary<Guid, WaveformState> _states = [];
    private readonly string _sessionSuffix = "-" + Guid.NewGuid().ToString("N")[..6];
    private MetricAliasMap? _aliases;
    private ISparkplugNode? _node;
    private CertificateSessionResource? _certResource;
    private bool _faulted;

    public Guid NodeId => config.Id;

    public NodeRuntimeStatus Status { get; private set; } = NodeRuntimeStatus.Idle;

    public async Task StartAsync()
    {
        if (_faulted)
            throw new InvalidOperationException(
                "Cannot restart a faulted SparkplugNodeRunner. The previous shutdown failed.");

        Status = NodeRuntimeStatus.Connecting;
        CertificateSessionResource? localCertResource = null;
        ISparkplugNode? localNode = null;

        try
        {
            if (connection.UseTls && connection.ClientCertificateAssetId is not null)
            {
                localCertResource = new CertificateSessionResource();
                var bundle = await certStore.LoadAsync(
                    connection.Id, connection.ClientCertificateAssetId).ConfigureAwait(false);
                if (bundle is null)
                {
                    Status = NodeRuntimeStatus.Error;
                    throw new CertificateAssetUnavailableException(connection.ClientCertificateAssetId);
                }
                localCertResource.Set(bundle.Certificate);
            }

            var birthMetrics = BuildBirthMetrics();

            localNode = config.UseMetricAliases
                ? nodeFactory.Create(birthMetrics, SparkplugSpecificationVersion.Version30,
                    [.. config.Devices.Select(d => d.DeviceId)],
                    deviceId => SampleDeviceMetrics(
                        config.Devices.First(d => string.Equals(d.DeviceId, deviceId, StringComparison.Ordinal)), 0, isBirth: true))
                : nodeFactory.Create(birthMetrics, SparkplugSpecificationVersion.Version30);
            await localNode.Start(SparkplugNodeOptionsBuilder.Build(
                connection, config, localCertResource, config.NodeId + _sessionSuffix)).ConfigureAwait(false);

            foreach (var device in config.Devices)
                await localNode.PublishDeviceBirthMessage(device.DeviceId, SampleDeviceMetrics(device, 0, isBirth: true)).ConfigureAwait(false);

            _certResource = localCertResource;
            _node = localNode;
            Status = NodeRuntimeStatus.Connected;
        }
        catch (Exception ex)
        {
            Status = NodeRuntimeStatus.Error;
            logger.LogError(ex, "Emulator node {NodeId} failed to connect to {Host}:{Port}",
                config.NodeId, connection.Host, connection.Port);

            DisposeAfterStartFailure(ex, localNode, localCertResource);

            throw;
        }
    }

    // Builds the alias map as a side effect: it has to be derived from the exact metric
    // list the NBIRTH carries, and every later publish depends on it already existing.
    private List<Metric> BuildBirthMetrics()
    {
        var nodeMetrics = new List<Metric>(initialKnownMetrics)
        {
            new("Node Control/Rebirth", DataType.Boolean, false)
        };

        _aliases = config.UseMetricAliases
            ? MetricAliasMap.Build(nodeMetrics, config.Devices)
            : null;

        return _aliases?.ApplyToNodeMetrics(nodeMetrics, isBirth: true) ?? nodeMetrics;
    }

    // localCertResource is passed by value deliberately: nulling it here only suppresses
    // the Dispose below, exactly as the inline version did, because a quarantined resource
    // is owned by the quarantine from that point on.
    private void DisposeAfterStartFailure(
        Exception ex, ISparkplugNode? localNode, CertificateSessionResource? localCertResource)
    {
        if (localNode is not null)
        {
            try { (localNode as IDisposable)?.Dispose(); }
            catch (Exception disposeEx)
            {
                _faulted = true;

                if (localCertResource is not null)
                {
                    quarantine.Quarantine(localCertResource,
                        $"StartAsync failed ({ex.Message}), node.Dispose also failed ({disposeEx.Message})");
                    localCertResource = null;
                }
            }
        }

        localCertResource?.Dispose();
    }

    public async Task PublishTickAsync(double tSeconds, IReadOnlyList<Metric> nodeHealthMetrics)
    {
        if (_node is not { IsConnected: true }) return;

        var healthMetrics = _aliases?.ApplyToNodeMetrics(nodeHealthMetrics, isBirth: false)
                            ?? [.. nodeHealthMetrics];
        var tasks = new List<Task> { _node.PublishMetrics(healthMetrics) };
        tasks.AddRange(config.Devices
            .Where(d => d.Metrics.Count > 0)
            .Select(d => _node.PublishDeviceMetrics(d.DeviceId, SampleDeviceMetrics(d, tSeconds, isBirth: false))));
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        if (_node is null) return;

        try
        {
            if (_node.IsConnected)
            {
                try
                {
                    await _node.PublishNodeDeathMessage().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    if (logger.IsEnabled(LogLevel.Debug))
                        logger.LogDebug(ex, "Could not publish NDEATH for node {NodeId} (connection already gone — LWT will handle it)", config.NodeId);
                }
            }

            await _node.Stop().ConfigureAwait(false);
            (_node as IDisposable)?.Dispose();
            _node = null;

            _certResource?.Dispose();
            _certResource = null;

            Status = NodeRuntimeStatus.Idle;
        }
        catch (Exception ex)
        {
            _faulted = true;
            Status = NodeRuntimeStatus.Error;
            logger.LogWarning(ex, "Emulator node {NodeId} failed to stop cleanly", config.NodeId);

            if (_certResource is not null)
            {
                quarantine.Quarantine(_certResource, $"StopAsync failed: {ex.Message}");
                _certResource = null;
            }

            // Best-effort teardown after a failed stop; the original failure is logged
            // above and rethrown below, so a second exception here would mask it.
            if (_node is not null)
            {
                try { await _node.PublishNodeDeathMessage().ConfigureAwait(false); } catch { /* broker may already be gone */ }
                try { (_node as IDisposable)?.Dispose(); } catch { /* node is being discarded regardless */ }
            }
            _node = null;

            throw;
        }
    }

    private List<Metric> SampleDeviceMetrics(EmulatorDeviceConfig device, double tSeconds, bool isBirth = false)
    {
        var metrics = new List<Metric>(device.Metrics.Count);
        foreach (var metric in device.Metrics)
        {
            if (!_states.TryGetValue(metric.Id, out var state))
            {
                state = WaveformSampler.CreateState(metric);
                _states[metric.Id] = state;
            }

            var value = WaveformSampler.Next(metric, state, tSeconds);
            var alias = _aliases?.DeviceAlias(device.DeviceId, metric.Name) ?? 0;
            metrics.Add(ToSparkplugMetric(metric, value, alias, isBirth));
        }

        return metrics;
    }

    private static Metric ToSparkplugMetric(EmulatorMetricConfig metric, double value, ulong alias = 0, bool isBirth = false)
    {
        var m = metric.ValueType switch
        {
            MetricValueType.Boolean => new Metric(metric.Name, DataType.Boolean, value >= 0.5),
            MetricValueType.Int64 => new Metric(metric.Name, DataType.Int64, (long)Math.Round(value)),
            _ => new Metric(metric.Name, DataType.Double, value)
        };

        if (alias != 0)
        {
            m.Alias = alias;
            if (!isBirth)
                m.Name = null!;
        }

        return m;
    }
}
