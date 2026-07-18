using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;
using MqttProbe.Models.Emulation;
using MqttProbe.Models.Mqtt;
using MqttProbe.Services.Metrics;
using MqttProbe.Services.Plugins.Contracts;
using MqttProbe.Services.Plugins.Pipeline;
using MqttProbe.Services.Security;
using MqttProbe.Services.Sparkplug;
using SparkplugNet.Core.Enumerations;
using SparkplugNet.Core.Node;
using SparkplugNet.VersionB.Data;

namespace MqttProbe.Services.Emulation;

public enum NodeRuntimeStatus { Idle, Connecting, Connected, Error }

public interface INodeRunner
{
    public Guid NodeId { get; }
    public NodeRuntimeStatus Status { get; }
    public Task StartAsync();
    public Task PublishTickAsync(double tSeconds, IReadOnlyList<Metric> nodeHealthMetrics);
    public Task StopAsync();
}

public class NodeHealthMetricsProvider(IAppHealthMetricsCollector collector)
{
    public List<Metric> BuildSnapshot(int publishersOnline, long publishCycles)
    {
        var health = collector.GetSnapshot();
        var metrics = new List<Metric>();
        if (health.CpuUsagePercent.HasValue)
            metrics.Add(new Metric("CPU Usage (%)", DataType.Double, health.CpuUsagePercent.Value));
        if (health.ManagedHeapMb.HasValue)
            metrics.Add(new Metric("Managed Heap (MB)", DataType.Double, health.ManagedHeapMb.Value));
        if (health.WorkingSetMb.HasValue)
            metrics.Add(new Metric("Working Set (MB)", DataType.Double, health.WorkingSetMb.Value));
        if (health.ThreadCount.HasValue)
            metrics.Add(new Metric("Thread Count", DataType.Double, (double)health.ThreadCount.Value));
        if (health.ThreadPoolQueueLength.HasValue)
            metrics.Add(new Metric("ThreadPool Queue", DataType.Double, (double)health.ThreadPoolQueueLength.Value));
        if (health.GcGen2Collections.HasValue)
            metrics.Add(new Metric("GC Gen2 Collections", DataType.Double, (double)health.GcGen2Collections.Value));
        if (health.UptimeSeconds.HasValue)
            metrics.Add(new Metric("Uptime (s)", DataType.Double, health.UptimeSeconds.Value));
        metrics.Add(new Metric("Publishers Online", DataType.Double, (double)publishersOnline));
        metrics.Add(new Metric("Publish Cycles", DataType.Double, (double)publishCycles));
        return metrics;
    }
}

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
    private Dictionary<string, ulong>? _nodeAliases;
    private Dictionary<string, Dictionary<string, ulong>>? _deviceAliases;
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
                    connection.Id, connection.ClientCertificateAssetId);
                if (bundle is null)
                {
                    Status = NodeRuntimeStatus.Error;
                    throw new CertificateAssetUnavailableException(connection.ClientCertificateAssetId);
                }
                localCertResource.Set(bundle.Certificate);
            }

            var nodeMetrics = new List<Metric>(initialKnownMetrics)
            {
                new("Node Control/Rebirth", DataType.Boolean, false)
            };
            BuildAliasMaps(nodeMetrics);

            var birthMetrics = nodeMetrics;
            if (_nodeAliases is not null)
            {
                birthMetrics = new List<Metric>(nodeMetrics.Count);
                foreach (var source in nodeMetrics)
                {
                    if (!_nodeAliases.TryGetValue(source.Name, out var alias))
                        throw new InvalidOperationException(
                            $"Missing alias for node metric '{source.Name}'.");

                    var m = new Metric(source.Name, source.DataType, source.Value);
                    m.Alias = alias;
                    birthMetrics.Add(m);
                }
            }

            localNode = config.UseMetricAliases
                ? nodeFactory.Create(birthMetrics, SparkplugSpecificationVersion.Version30,
                    config.Devices.Select(d => d.DeviceId).ToList(),
                    deviceId => SampleDeviceMetrics(
                        config.Devices.First(d => d.DeviceId == deviceId), 0, isBirth: true))
                : nodeFactory.Create(birthMetrics, SparkplugSpecificationVersion.Version30);
            await localNode.Start(BuildNodeOptions(
                connection, config, localCertResource, config.NodeId + _sessionSuffix));

            foreach (var device in config.Devices)
                await localNode.PublishDeviceBirthMessage(device.DeviceId, SampleDeviceMetrics(device, 0, isBirth: true));

            _certResource = localCertResource;
            _node = localNode;
            Status = NodeRuntimeStatus.Connected;
        }
        catch (Exception ex)
        {
            Status = NodeRuntimeStatus.Error;
            logger.LogError(ex, "Emulator node {NodeId} failed to connect to {Host}:{Port}",
                config.NodeId, connection.Host, connection.Port);

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

            throw;
        }
    }

    public async Task PublishTickAsync(double tSeconds, IReadOnlyList<Metric> nodeHealthMetrics)
    {
        if (_node is not { IsConnected: true }) return;

        var healthMetrics = ApplyNodeAliases(nodeHealthMetrics, isBirth: false);
        var tasks = new List<Task> { _node.PublishMetrics(healthMetrics) };
        tasks.AddRange(config.Devices
            .Where(d => d.Metrics.Count > 0)
            .Select(d => _node.PublishDeviceMetrics(d.DeviceId, SampleDeviceMetrics(d, tSeconds, isBirth: false))));
        await Task.WhenAll(tasks);
    }

    public async Task StopAsync()
    {
        if (_node is null) return;

        try
        {
            if (_node.IsConnected)
            {
                try { await _node.PublishNodeDeathMessage(); }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Could not publish NDEATH for node {NodeId} (connection already gone — LWT will handle it)", config.NodeId);
                }
            }

            await _node.Stop();
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

            try { await _node!.PublishNodeDeathMessage(); } catch { }
            try { (_node as IDisposable)?.Dispose(); } catch { }
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
            ulong alias = 0;
            if (_deviceAliases is not null)
            {
                if (!_deviceAliases.TryGetValue(device.DeviceId, out var deviceMap)
                    || !deviceMap.TryGetValue(metric.Name, out alias))
                {
                    throw new InvalidOperationException(
                        $"Missing alias for metric '{metric.Name}' in device '{device.DeviceId}'. " +
                        "Alias maps may be out of sync with config.");
                }
            }

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

    private void BuildAliasMaps(List<Metric> nodeMetrics)
    {
        if (!config.UseMetricAliases)
        {
            _nodeAliases = null;
            _deviceAliases = null;
            return;
        }

        _nodeAliases = new Dictionary<string, ulong>();
        ulong alias = 1;
        foreach (var metric in nodeMetrics)
        {
            _nodeAliases[metric.Name!] = alias++;
        }

        _deviceAliases = new Dictionary<string, Dictionary<string, ulong>>();
        foreach (var device in config.Devices)
        {
            var deviceMap = new Dictionary<string, ulong>();
            ulong deviceAlias = 1;
            foreach (var metric in device.Metrics)
            {
                deviceMap[metric.Name] = deviceAlias++;
            }

            _deviceAliases[device.DeviceId] = deviceMap;
        }

        ValidateAliasMaps();
    }

    private void ValidateAliasMaps()
    {
        if (_nodeAliases is null) return;

        if (_nodeAliases.Values.Any(v => v == 0))
            throw new InvalidOperationException(
                "Node alias map contains alias 0, which is reserved. All aliases must be >= 1.");

        if (_nodeAliases.Values.Distinct().Count() != _nodeAliases.Values.Count)
            throw new InvalidOperationException(
                "Node alias map contains duplicate alias values.");

        if (_deviceAliases is not null)
        {
            foreach (var (deviceId, aliases) in _deviceAliases)
            {
                if (aliases.Values.Any(v => v == 0))
                    throw new InvalidOperationException(
                        $"Device '{deviceId}' alias map contains alias 0, which is reserved.");

                if (aliases.Values.Distinct().Count() != aliases.Values.Count)
                    throw new InvalidOperationException(
                        $"Device '{deviceId}' alias map contains duplicate alias values.");
            }
        }
    }

    private List<Metric> ApplyNodeAliases(IReadOnlyList<Metric> metrics, bool isBirth)
    {
        if (_nodeAliases is null) return new List<Metric>(metrics);

        var result = new List<Metric>(metrics.Count);
        foreach (var source in metrics)
        {
            if (!_nodeAliases.TryGetValue(source.Name, out var alias))
                throw new InvalidOperationException(
                    $"Missing alias for node metric '{source.Name}'. " +
                    "Alias map may be out of sync with config.");

            var m = new Metric(source.Name, source.DataType, source.Value);
            m.Alias = alias;
            if (!isBirth)
                m.Name = null!;
            result.Add(m);
        }

        return result;
    }

    internal static SparkplugNodeOptions BuildNodeOptions(
        Connection connection, EmulatorNodeConfig config,
        CertificateSessionResource? certResource = null,
        string? mqttClientId = null)
    {
        MqttClientWebSocketOptions? webSocketOptions = null;
        var brokerAddress = connection.Host;
        if (connection.Protocol == Protocol.WebSocket)
        {
            var scheme = connection.UseTls ? "wss" : "ws";
            var wsPath = (connection.WebsocketBasePath ?? string.Empty).Trim().TrimStart('/');
            brokerAddress = string.IsNullOrEmpty(wsPath)
                ? $"{scheme}://{connection.Host}:{connection.Port}/"
                : $"{scheme}://{connection.Host}:{connection.Port}/{wsPath}";
            webSocketOptions = new MqttClientWebSocketOptions { Uri = brokerAddress };
        }

        var tlsOptions = new MqttClientTlsOptions();
        if (connection.UseTls)
        {
            var tlsBuilder = new MqttClientTlsOptionsBuilder()
                .WithSslProtocols(System.Security.Authentication.SslProtocols.Tls12 |
                                  System.Security.Authentication.SslProtocols.Tls13);
            if (connection.AllowUntrustedCertificate)
                tlsBuilder = tlsBuilder.WithAllowUntrustedCertificates()
                                       .WithCertificateValidationHandler(_ => true);
            if (certResource?.Certificate is not null)
                tlsBuilder = tlsBuilder.WithClientCertificates(
                    new X509Certificate2Collection(certResource.Certificate));
            tlsOptions = tlsBuilder.Build();
        }

        mqttClientId ??= config.NodeId + "-" + Guid.NewGuid().ToString("N")[..6];
        var reconnectSeconds = connection.ReconnectDelay > 0 ? connection.ReconnectDelay : 5;

        return new SparkplugNodeOptions(
            brokerAddress,
            connection.Port,
            mqttClientId,
            connection.User,
            connection.Password,
            null,
            TimeSpan.FromSeconds(reconnectSeconds),
            SparkplugMqttProtocolVersion.V311,
            tlsOptions,
            webSocketOptions,
            config.GroupId,
            config.NodeId,
            CancellationToken.None);
    }
}

public class GenericNodeRunner(EmulatorNodeConfig config, IManagedMqttClient managedMqttClient, PayloadPipeline pipeline, ILogger logger) : INodeRunner
{
    private readonly Dictionary<Guid, WaveformState> _states = [];

    public Guid NodeId => config.Id;

    public NodeRuntimeStatus Status { get; private set; } = NodeRuntimeStatus.Idle;

    public Task StartAsync()
    {
        Status = NodeRuntimeStatus.Connected;
        return Task.CompletedTask;
    }

    public async Task PublishTickAsync(double tSeconds, IReadOnlyList<Metric> nodeHealthMetrics)
    {
        try
        {
            foreach (var device in config.Devices.Where(d => d.Metrics.Count > 0))
            {
                if (config.PayloadFormatId == "json")
                {
                    var metrics = new Dictionary<string, object>(device.Metrics.Count);
                    foreach (var m in device.Metrics)
                    {
                        var value = WaveformSampler.Next(m, State(m), tSeconds);
                        metrics[m.Name] = m.ValueType switch
                        {
                            MetricValueType.Boolean => value >= 0.5,
                            MetricValueType.Int64 => (long)Math.Round(value),
                            _ => value
                        };
                    }

                    var request = new PayloadEncoderRequest
                    {
                        Topic = TopicTemplateRenderer.RenderDeviceTopic(config, device.DeviceId),
                        FormatId = config.PayloadFormatId,
                        Metrics = metrics,
                        TimestampUtc = DateTime.UtcNow
                    };
                    var bytes = pipeline.EncodeOutbound(request);
                    await EnqueueAsync(request.Topic, bytes);
                }
                else
                {
                    foreach (var metric in device.Metrics)
                    {
                        var value = WaveformSampler.Next(metric, State(metric), tSeconds);
                        var objectValue = (object)(metric.ValueType switch
                        {
                            MetricValueType.Boolean => value >= 0.5,
                            MetricValueType.Int64 => (long)Math.Round(value),
                            _ => value
                        });

                        var request = new PayloadEncoderRequest
                        {
                            Topic = TopicTemplateRenderer.RenderMetricTopic(config, device.DeviceId, metric.Name),
                            FormatId = config.PayloadFormatId,
                            Metrics = new Dictionary<string, object> { [metric.Name] = objectValue },
                            TimestampUtc = DateTime.UtcNow
                        };
                        var bytes = pipeline.EncodeOutbound(request);
                        await EnqueueAsync(request.Topic, bytes);
                    }
                }
            }
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex,
                "No encoder found for format '{FormatId}' on node '{NodeId}'; skipping publish for this tick",
                config.PayloadFormatId, config.NodeId);
        }
    }

    public Task StopAsync()
    {
        Status = NodeRuntimeStatus.Idle;
        return Task.CompletedTask;
    }

    private WaveformState State(EmulatorMetricConfig metric)
    {
        if (!_states.TryGetValue(metric.Id, out var state))
        {
            state = WaveformSampler.CreateState(metric);
            _states[metric.Id] = state;
        }

        return state;
    }

    private Task EnqueueAsync(string topic, byte[] payload) =>
        managedMqttClient.EnqueueAsync(new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .Build());
}
