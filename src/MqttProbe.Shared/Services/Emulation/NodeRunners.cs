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
            var node = nodeFactory.Create(nodeMetrics, SparkplugSpecificationVersion.Version30);
            await node.Start(BuildNodeOptions(connection, config));
            _node = node;
            foreach (var device in config.Devices)
                await node.PublishDeviceBirthMessage(device.DeviceId, SampleDeviceMetrics(device, 0));
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

        var tasks = new List<Task> { _node.PublishMetrics(new List<Metric>(nodeHealthMetrics)) };
        tasks.AddRange(config.Devices
            .Where(d => d.Metrics.Count > 0)
            .Select(d => _node.PublishDeviceMetrics(d.DeviceId, SampleDeviceMetrics(d, tSeconds))));
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

    private List<Metric> SampleDeviceMetrics(EmulatorDeviceConfig device, double tSeconds)
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
            metrics.Add(ToSparkplugMetric(metric, value));
        }

        return metrics;
    }

    private static Metric ToSparkplugMetric(EmulatorMetricConfig metric, double value) =>
        metric.ValueType switch
        {
            MetricValueType.Boolean => new Metric(metric.Name, DataType.Boolean, value >= 0.5),
            MetricValueType.Int64 => new Metric(metric.Name, DataType.Int64, (long)Math.Round(value)),
            _ => new Metric(metric.Name, DataType.Double, value)
        };

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
