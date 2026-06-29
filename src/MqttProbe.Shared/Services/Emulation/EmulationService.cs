using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;
using MqttProbe.Models.Emulation;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Mqtt;
using MqttProbe.Services.Sparkplug;

namespace MqttProbe.Services.Emulation;

public interface IEmulationService : IDisposable
{
    public IReadOnlyList<EmulatorNodeConfig> Nodes { get; }
    public int PublishIntervalMs { get; }
    public bool IsRunning { get; }
    public event Action? StateChanged;

    public void SetConnection(Guid connectionId);
    public Task ResetForConnectionAsync(Guid connectionId);
    public Task AddNodeAsync(EmulatorNodeConfig node);
    public Task UpdateNodeAsync(EmulatorNodeConfig node);
    public Task RemoveNodeAsync(Guid nodeId);
    public Task RemoveAllNodesAsync();
    public Task<IReadOnlyList<EmulatorNodeConfig>> DuplicateNodeAsync(Guid nodeId, int copies);
    public Task SetPublishIntervalAsync(int intervalMs);
    public Task StartAsync();
    public Task StopAsync();
    public NodeRuntimeStatus GetStatus(Guid nodeId);
}

public class EmulationService : IEmulationService
{
    private readonly ISettingsStore _settingsStore;
    private readonly ISparkplugNodeFactory _nodeFactory;
    private readonly ISessionState _sessionState;
    private readonly IManagedMqttClient _managedMqttClient;
    private readonly ILogger<EmulationService> _logger;
    private readonly NodeHealthMetricsProvider _healthMetrics = new();

    private List<INodeRunner> _runners = [];
    private CancellationTokenSource? _cts;
    private Task? _publishLoop;
    private long _publishCycles;
    private long _loopStartTimestamp;
    private bool _disposed;
    private Guid _connectionId;

    public EmulationService(ISettingsStore settingsStore,
        ISparkplugNodeFactory nodeFactory,
        ISessionState sessionState,
        IManagedMqttClient managedMqttClient,
        ILogger<EmulationService> logger)
    {
        _settingsStore = settingsStore;
        _nodeFactory = nodeFactory;
        _sessionState = sessionState;
        _managedMqttClient = managedMqttClient;
        _logger = logger;
        _settingsStore.EmulatorsChanged += OnEmulatorsChanged;
        _managedMqttClient.DisconnectedAsync += OnMainClientDisconnected;
    }

    public event Action? StateChanged;

    public IReadOnlyList<EmulatorNodeConfig> Nodes => _settingsStore.GetEmulatorNodes(_connectionId);

    public int PublishIntervalMs => _settingsStore.GetEmulatorPublishIntervalMs(_connectionId);

    public bool IsRunning => _cts is { IsCancellationRequested: false };

    public void SetConnection(Guid connectionId)
    {
        _connectionId = connectionId;
        StateChanged?.Invoke();
    }

    public async Task ResetForConnectionAsync(Guid connectionId)
    {
        if (IsRunning)
            await StopAsync();

        _connectionId = connectionId;
        StateChanged?.Invoke();
    }

    public async Task AddNodeAsync(EmulatorNodeConfig node)
    {
        ThrowIfRunning();
        await _settingsStore.AddEmulatorNodeAsync(_connectionId, node);
    }

    public async Task UpdateNodeAsync(EmulatorNodeConfig node)
    {
        ThrowIfRunning();
        await _settingsStore.UpdateEmulatorNodeAsync(_connectionId, node);
    }

    public async Task RemoveNodeAsync(Guid nodeId)
    {
        ThrowIfRunning();
        await _settingsStore.RemoveEmulatorNodeAsync(_connectionId, nodeId);
    }

    public async Task RemoveAllNodesAsync()
    {
        ThrowIfRunning();
        await _settingsStore.RemoveAllEmulatorNodesAsync(_connectionId);
    }

    public async Task<IReadOnlyList<EmulatorNodeConfig>> DuplicateNodeAsync(Guid nodeId, int copies)
    {
        ThrowIfRunning();
        var source = _settingsStore.GetEmulatorNodes(_connectionId).FirstOrDefault(n => n.Id == nodeId)
            ?? throw new ArgumentException($"No emulator node with id {nodeId}.", nameof(nodeId));

        var names = GenerateCopyNames(source.NodeId, source.GroupId, _settingsStore.GetEmulatorNodes(_connectionId), copies);
        var created = new List<EmulatorNodeConfig>(copies);
        foreach (var name in names)
        {
            var clone = CloneWithFreshIds(source);
            clone.NodeId = name;
            created.Add(clone);
            await _settingsStore.AddEmulatorNodeAsync(_connectionId, clone);
        }

        return created;
    }

    public async Task SetPublishIntervalAsync(int intervalMs)
    {
        ThrowIfRunning();
        await _settingsStore.SetEmulatorPublishIntervalAsync(_connectionId, intervalMs);
    }

    public async Task StartAsync()
    {
        if (IsRunning) return;

        // Runners work on a deep snapshot so config edits from other circuits never tear a running loop.
        var snapshot = CloneNodes(_settingsStore.GetEmulatorNodes(_connectionId));
        var intervalMs = _settingsStore.GetEmulatorPublishIntervalMs(_connectionId);
        var connection = _sessionState.SelectedConnection;
        var sparkplugCount = snapshot.Count(n => n.Type == EmulatorNodeType.SparkplugB);
        var initialKnownMetrics = _healthMetrics.BuildSnapshot(sparkplugCount, 0);

        _runners = snapshot
            .Select(node => (INodeRunner)(node.Type == EmulatorNodeType.SparkplugB
                ? new SparkplugNodeRunner(node, _nodeFactory, connection, initialKnownMetrics, _logger)
                : new GenericNodeRunner(node, _managedMqttClient)))
            .ToList();

        await Parallel.ForEachAsync(_runners, async (runner, _) => await runner.StartAsync());

        _publishCycles = 0;
        _loopStartTimestamp = Stopwatch.GetTimestamp();
        await TickSafelyAsync();
        _cts = new CancellationTokenSource();
        _publishLoop = RunPublishLoop(intervalMs, _cts.Token);
        StateChanged?.Invoke();
    }

    public async Task StopAsync()
    {
        var wasRunning = IsRunning;
        await StopPublishLoop();

        var runners = _runners;
        if (runners.Count > 0)
        {
            await Task.WhenAll(runners.Select(r => r.StopAsync()));
            _runners = [];
        }

        if (wasRunning || runners.Count > 0)
            StateChanged?.Invoke();
    }

    public NodeRuntimeStatus GetStatus(Guid nodeId) =>
        _runners.FirstOrDefault(r => r.NodeId == nodeId)?.Status ?? NodeRuntimeStatus.Idle;

    public static List<string> GenerateCopyNames(
        string sourceNodeId,
        string groupId,
        IEnumerable<EmulatorNodeConfig> existingNodes,
        int copies) =>
        GenerateCopyNames(
            sourceNodeId,
            existingNodes.Where(n => n.GroupId == groupId).Select(n => n.NodeId),
            copies);

    public static List<string> GenerateCopyNames(string sourceName, IEnumerable<string> takenNames, int copies)
    {
        var (stem, next, padWidth) = ParseNodeIdSuffix(sourceName);
        var taken = takenNames.ToHashSet(StringComparer.Ordinal);

        var names = new List<string>(Math.Max(0, copies));
        for (var i = 0; i < copies; i++)
        {
            string candidate;
            do
            {
                candidate = $"{stem}-{next.ToString(CultureInfo.InvariantCulture).PadLeft(padWidth, '0')}";
                next++;
            }
            while (taken.Contains(candidate));

            taken.Add(candidate);
            names.Add(candidate);
        }

        return names;
    }

    private static (string Stem, int Next, int PadWidth) ParseNodeIdSuffix(string nodeId)
    {
        var dashIdx = nodeId.LastIndexOf('-');
        if (dashIdx > 0 && dashIdx < nodeId.Length - 1)
        {
            var digits = nodeId[(dashIdx + 1)..];
            if (digits.All(char.IsAsciiDigit) && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                return (nodeId[..dashIdx], n + 1, digits.Length);
        }

        return (nodeId, 2, 1);
    }

    private static EmulatorNodeConfig CloneWithFreshIds(EmulatorNodeConfig source)
    {
        var clone = JsonSerializer.Deserialize<EmulatorNodeConfig>(JsonSerializer.Serialize(source))!;
        clone.Id = Guid.NewGuid();
        foreach (var device in clone.Devices)
        {
            device.Id = Guid.NewGuid();
            foreach (var metric in device.Metrics)
                metric.Id = Guid.NewGuid();
        }

        return clone;
    }

    private static List<EmulatorNodeConfig> CloneNodes(IReadOnlyList<EmulatorNodeConfig> nodes) =>
        JsonSerializer.Deserialize<List<EmulatorNodeConfig>>(JsonSerializer.Serialize(nodes)) ?? [];

    private void ThrowIfRunning()
    {
        if (IsRunning)
            throw new InvalidOperationException("Emulator configuration is locked while the emulator is running.");
    }

    private async Task RunPublishLoop(int rateMs, CancellationToken ct)
    {
        // The first tick already ran inline during StartAsync, so the loop always delays before publishing.
        var lastTickDuration = TimeSpan.Zero;
        while (!ct.IsCancellationRequested)
        {
            var remaining = TimeSpan.FromMilliseconds(rateMs) - lastTickDuration;
            if (remaining > TimeSpan.Zero)
            {
                try { await Task.Delay(remaining, ct); }
                catch (OperationCanceledException) { break; }
            }

            if (ct.IsCancellationRequested) break;

            var start = Stopwatch.GetTimestamp();
            await TickSafelyAsync();
            lastTickDuration = Stopwatch.GetElapsedTime(start);
        }
    }

    private async Task TickSafelyAsync()
    {
        try
        {
            Interlocked.Increment(ref _publishCycles);
            var tSeconds = Stopwatch.GetElapsedTime(_loopStartTimestamp).TotalSeconds;
            var runners = _runners;
            var publishersOnline = runners.Count(r => r is SparkplugNodeRunner && r.Status == NodeRuntimeStatus.Connected);
            var health = _healthMetrics.BuildSnapshot(publishersOnline, Interlocked.Read(ref _publishCycles));
            await Task.WhenAll(runners.Select(r => r.PublishTickAsync(tSeconds, health)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during emulator publish tick");
        }
    }

    private async Task StopPublishLoop()
    {
        if (_cts == null) return;
        await _cts.CancelAsync();
        if (_publishLoop != null)
        {
            try { await _publishLoop.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
        }

        _cts.Dispose();
        _cts = null;
        _publishLoop = null;
    }

    private void OnEmulatorsChanged(Guid connectionId)
    {
        if (connectionId != _connectionId) return;
        StateChanged?.Invoke();
    }

    private async Task OnMainClientDisconnected(MqttClientDisconnectedEventArgs args)
    {
        if (!IsRunning) return;
        _logger.LogInformation("Main MQTT client disconnected — stopping emulation");
        await StopAsync();
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing)
        {
            _settingsStore.EmulatorsChanged -= OnEmulatorsChanged;
            _managedMqttClient.DisconnectedAsync -= OnMainClientDisconnected;
            _cts?.Cancel();
            if (_publishLoop != null)
            {
                try { _publishLoop.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { }
                catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is OperationCanceledException)) { }
            }

            _cts?.Dispose();
            _cts = null;
            _publishLoop = null;
        }

        _disposed = true;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}
