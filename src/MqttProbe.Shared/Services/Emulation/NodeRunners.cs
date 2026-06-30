using System.Diagnostics;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;
using MqttProbe.Models.Emulation;
using MqttProbe.Models.Mqtt;
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

public class NodeHealthMetricsProvider
{
    private readonly Lock _sync = new();
    private readonly DateTime _startTime = DateTime.UtcNow;
    private readonly Process _currentProcess = Process.GetCurrentProcess();
    private DateTime _lastCpuSample = DateTime.UtcNow;
    private TimeSpan _lastCpuTime = Process.GetCurrentProcess().TotalProcessorTime;

    public List<Metric> BuildSnapshot(int publishersOnline, long publishCycles)
    {
        lock (_sync)
        {
            _currentProcess.Refresh();
            var now = DateTime.UtcNow;
            var cpuTime = _currentProcess.TotalProcessorTime;
            var elapsedSec = (now - _lastCpuSample).TotalSeconds;
            var cpuUsage = elapsedSec > 0
                ? Math.Min(100.0, (cpuTime - _lastCpuTime).TotalSeconds / (elapsedSec * Environment.ProcessorCount) * 100.0)
                : 0.0;
            _lastCpuSample = now;
            _lastCpuTime = cpuTime;

            return
            [
                new Metric("CPU Usage (%)", DataType.Double, cpuUsage),
                new Metric("Managed Heap (MB)", DataType.Double, GC.GetTotalMemory(false) / 1048576.0),
                new Metric("Working Set (MB)", DataType.Double, _currentProcess.WorkingSet64 / 1048576.0),
                new Metric("Thread Count", DataType.Double, (double)_currentProcess.Threads.Count),
                new Metric("ThreadPool Queue", DataType.Double, (double)ThreadPool.PendingWorkItemCount),
                new Metric("GC Gen2 Collections", DataType.Double, (double)GC.CollectionCount(2)),
                new Metric("Uptime (s)", DataType.Double, (now - _startTime).TotalSeconds),
                new Metric("Publishers Online", DataType.Double, (double)publishersOnline),
                new Metric("Publish Cycles", DataType.Double, (double)publishCycles)
            ];
        }
    }
}

public class SparkplugNodeRunner(
    EmulatorNodeConfig config,
    ISparkplugNodeFactory nodeFactory,
    Connection connection,
    IReadOnlyList<Metric> initialKnownMetrics,
    ILogger logger) : INodeRunner
{
    private readonly Dictionary<Guid, WaveformState> _states = [];
    private Dictionary<string, ulong>? _nodeAliases;
    private Dictionary<string, Dictionary<string, ulong>>? _deviceAliases;
    private ISparkplugNode? _node;

    public Guid NodeId => config.Id;

    public NodeRuntimeStatus Status { get; private set; } = NodeRuntimeStatus.Idle;

    public async Task StartAsync()
    {
        Status = NodeRuntimeStatus.Connecting;
        try
        {
            var nodeMetrics = new List<Metric>(initialKnownMetrics)
            {
                new("Node Control/Rebirth", DataType.Boolean, false)
            };
            BuildAliasMaps(nodeMetrics);

            // Rebuild with birth-mode aliases before passing to factory.
            // SparkplugNet uses these knownMetrics for NBIRTH.
            var birthMetrics = nodeMetrics;
            if (_nodeAliases is not null)
            {
                birthMetrics = new List<Metric>(nodeMetrics.Count);
                foreach (var source in nodeMetrics)
                {
                    if (!_nodeAliases.TryGetValue(source.Name, out var alias))
                        throw new InvalidOperationException(
                            $"Missing alias for node metric '{source.Name}'.");

                    // Rebuild from scratch using the (name, DataType, value) constructor.
                    var m = new Metric(source.Name, source.DataType, source.Value);
                    m.Alias = alias;
                    birthMetrics.Add(m);
                }
            }

            var node = nodeFactory.Create(birthMetrics, SparkplugSpecificationVersion.Version30);
            await node.Start(BuildNodeOptions(connection, config));
            _node = node;
            foreach (var device in config.Devices)
                await node.PublishDeviceBirthMessage(device.DeviceId, SampleDeviceMetrics(device, 0, isBirth: true));
            Status = NodeRuntimeStatus.Connected;
        }
        catch (Exception ex)
        {
            Status = NodeRuntimeStatus.Error;
            logger.LogError(ex, "Emulator node {NodeId} failed to connect to {Host}:{Port}",
                config.NodeId, connection.Host, connection.Port);
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
        if (_node is not null)
        {
            try
            {
                if (_node.IsConnected)
                    await _node.PublishNodeDeathMessage();
                await _node.Stop();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Emulator node {NodeId} failed to stop cleanly", config.NodeId);
            }
        }

        _node = null;
        Status = NodeRuntimeStatus.Idle;
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
                m.Name = null!; // Data mode: alias-only (CS8625: SparkplugNet Name is non-nullable but null serializes as field omission)
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

        // Node-scoped aliases: health metrics + Node Control/Rebirth
        _nodeAliases = new Dictionary<string, ulong>();
        ulong alias = 1;
        foreach (var metric in nodeMetrics)
        {
            _nodeAliases[metric.Name!] = alias++;
        }

        // Device-scoped aliases: per device, starting at 1
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

            // Rebuild from scratch — SparkplugNet Metric has no copy constructor.
            // Use the same (name, DataType, value) constructor as ToSparkplugMetric.
            var m = new Metric(source.Name, source.DataType, source.Value);
            m.Alias = alias;
            if (!isBirth)
                m.Name = null!; // Data mode: alias-only (CS8625: SparkplugNet Name is non-nullable but null serializes as field omission)
            result.Add(m);
        }

        return result;
    }

    internal static SparkplugNodeOptions BuildNodeOptions(Connection connection, EmulatorNodeConfig config)
    {
        MqttClientWebSocketOptions? webSocketOptions = null;
        var brokerAddress = connection.Host;
        if (connection.Protocol == Protocol.WebSocket)
        {
            var scheme = connection.UseTls ? "wss" : "ws";
            // SparkplugNet 1.3.10 connects WebSocket nodes with WithUri(BrokerAddress) and ignores
            // MqttClientWebSocketOptions.Uri, so the full ws(s) URI must travel through BrokerAddress.
            // The options object is still passed (non-null) purely to select the library's WebSocket
            // branch over TCP.
            brokerAddress = $"{scheme}://{connection.Host}:{connection.Port}/{connection.WebsocketBasePath}";
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
            tlsOptions = tlsBuilder.Build();
        }

        return new SparkplugNodeOptions(
            brokerAddress,
            connection.Port,
            config.NodeId,
            connection.User,
            connection.Password,
            null,
            TimeSpan.FromSeconds(5),
            SparkplugMqttProtocolVersion.V311,
            tlsOptions,
            webSocketOptions,
            config.GroupId,
            config.NodeId,
            CancellationToken.None);
    }
}

public class GenericNodeRunner(EmulatorNodeConfig config, IManagedMqttClient managedMqttClient) : INodeRunner
{
    private readonly Dictionary<Guid, WaveformState> _states = [];

    public Guid NodeId => config.Id;

    public NodeRuntimeStatus Status { get; private set; } = NodeRuntimeStatus.Idle;

    public Task StartAsync()
    {
        // Generic nodes publish through the session's shared client, so there is no connection to open.
        Status = NodeRuntimeStatus.Connected;
        return Task.CompletedTask;
    }

    public async Task PublishTickAsync(double tSeconds, IReadOnlyList<Metric> nodeHealthMetrics)
    {
        foreach (var device in config.Devices.Where(d => d.Metrics.Count > 0))
        {
            if (config.PayloadFormat == GenericPayloadFormat.Json)
            {
                var values = device.Metrics
                    .Select(m => (Metric: m, Value: WaveformSampler.Next(m, State(m), tSeconds)))
                    .ToList();
                await EnqueueAsync(
                    TopicTemplateRenderer.RenderDeviceTopic(config, device.DeviceId),
                    GenericPayloadFormatter.FormatDeviceJson(DateTime.UtcNow, values));
            }
            else
            {
                foreach (var metric in device.Metrics)
                {
                    var value = WaveformSampler.Next(metric, State(metric), tSeconds);
                    var payload = config.PayloadFormat == GenericPayloadFormat.Hex
                        ? GenericPayloadFormatter.FormatHex(metric, value)
                        : GenericPayloadFormatter.FormatPlainText(metric, value);
                    await EnqueueAsync(
                        TopicTemplateRenderer.RenderMetricTopic(config, device.DeviceId, metric.Name),
                        payload);
                }
            }
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

    private Task EnqueueAsync(string topic, string payload) =>
        // QoS 0 and no retain are the MqttApplicationMessageBuilder defaults.
        managedMqttClient.EnqueueAsync(new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .Build());
}
