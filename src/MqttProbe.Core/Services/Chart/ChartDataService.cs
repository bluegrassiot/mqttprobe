using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MqttProbe.Core.Models.Chart;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Core.Services.Plugins.Pipeline;
using MqttProbe.Core.Services.Sparkplug;

namespace MqttProbe.Core.Services.Chart;

public interface IChartDataService : IDisposable
{
    public IReadOnlyList<ChartDataPoint> GetPoints(Guid seriesId);
    public event Action? OnDataUpdated;
    public Task StartAsync();
    public Task StopAsync();
    public bool IsListening { get; }
    public void SetConnection(Guid connectionId);
    public void ClearBuffers();
}

public class ChartDataService(
    IMqttManagedClient client,
    IJsonFieldExtractor extractor,
    IChartFieldRegistry registry,
    IChartSettings chartSettings,
    PayloadPipeline pipeline,
    ISparkplugSettings sparkplugSettings,
    ISparkplugTopologyService? topologyService = null,
    ILogger<ChartDataService>? logger = null,
    ITopicExcludeService? topicExcludeService = null)
    : IChartDataService
{
    private readonly ConcurrentDictionary<Guid, ConcurrentQueue<ChartDataPoint>> _buffers = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _stateLock = new();
    private ImmutableDictionary<Guid, SeriesIdentity> _sources = ImmutableDictionary<Guid, SeriesIdentity>.Empty;
    private Guid _connectionId;
    private bool _sourcesInitialized;
    private long _epoch;
    private int _exclusionHooks;
    private int _purgedNotify;
    private readonly ITopicExcludeService? _topicExcludeService = topicExcludeService;

    public event Action? OnDataUpdated;
    public bool IsListening { get; private set; }

    public void SetConnection(Guid connectionId)
    {
        lock (_stateLock)
        {
            if (_connectionId == connectionId) return;

            _connectionId = connectionId;
            _epoch++;
            _buffers.Clear();
            _sources = CaptureSources(connectionId);
            _sourcesInitialized = true;
        }
    }

    public void ClearBuffers()
    {
        lock (_stateLock)
        {
            _epoch++;
            _buffers.Clear();
        }

        OnDataUpdated?.Invoke();
    }

    public IReadOnlyList<ChartDataPoint> GetPoints(Guid seriesId) =>
        _buffers.TryGetValue(seriesId, out var q) ? [.. q] : [];

    public async Task StartAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (IsListening) return;

            chartSettings.ChartsChanged += OnChartsChanged;

            lock (_stateLock)
            {
                // Without a snapshot the first message captures current charts and sees
                // stopped-time edits anyway; only a captured map can be stale.
                if (_sourcesInitialized)
                    _sources = ReconcileSources(_connectionId);
            }

            client.ApplicationMessageReceivedAsync += MessageHandler;
            AttachExclusionHooks();
            IsListening = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!IsListening) return;
            client.ApplicationMessageReceivedAsync -= MessageHandler;
            chartSettings.ChartsChanged -= OnChartsChanged;

            // Exclusion hooks deliberately survive StopAsync: stopping listening must not
            // blind the service to purges, or a handler admitted before the stop can write
            // registry and buffer data that then outlives the exclusion into the next start.
            IsListening = false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void AttachExclusionHooks()
    {
        if (_topicExcludeService is null)
            return;

        if (Interlocked.Exchange(ref _exclusionHooks, 1) != 0)
            return;

        _topicExcludeService.PurgeExcludedTopic += OnPurgeExcludedTopic;
        _topicExcludeService.TopicExcluded += OnTopicExcluded;
        _topicExcludeService.PurgeExcludedSparkplug += OnPurgeExcludedSparkplug;
        _topicExcludeService.SparkplugExcluded += OnSparkplugExcluded;
    }

    private void DetachExclusionHooks()
    {
        if (_topicExcludeService is null)
            return;

        if (Interlocked.Exchange(ref _exclusionHooks, 0) == 0)
            return;

        _topicExcludeService.PurgeExcludedTopic -= OnPurgeExcludedTopic;
        _topicExcludeService.TopicExcluded -= OnTopicExcluded;
        _topicExcludeService.PurgeExcludedSparkplug -= OnPurgeExcludedSparkplug;
        _topicExcludeService.SparkplugExcluded -= OnSparkplugExcluded;
    }

    private Task MessageHandler(MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;
        var state = CaptureHandlerState();
        IDisposable? exclusionGate = null;
        if (_topicExcludeService is { } excludes)
        {
            exclusionGate = excludes.TryEnter(topic);
            if (exclusionGate is null)
                return Task.CompletedTask;
        }

        try
        {
            var result = pipeline.ProcessInbound(e);
            var payload = result.Envelope.DisplayText;

            IReadOnlyDictionary<ulong, string>? aliasNames = null;
            if (result.Envelope.FormatId == "sparkplug-b"
                && !result.Envelope.IsFailure
                && sparkplugSettings.Sparkplug.EnrichAliasNames
                && topologyService is not null)
            {
                aliasNames = SparkplugAliasResolver.Resolve(topic, result.Envelope, topologyService.Groups);
            }

            if (!TryExtractFields(payload, aliasNames, out var fields))
                return Task.CompletedTask;

            registry.Update(topic, fields);

            var updated = UpdateBuffers(state, topic, fields, DateTime.UtcNow);

            // Released before the notification: a subscriber that calls Add would wait on this gate.
            exclusionGate?.Dispose();
            exclusionGate = null;
            if (updated) OnDataUpdated?.Invoke();
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Error processing chart data message on topic {Topic}", e.ApplicationMessage.Topic);
        }
        finally
        {
            exclusionGate?.Dispose();
        }

        return Task.CompletedTask;
    }

    private void OnPurgeExcludedTopic(string filter) =>
        MarkPurged(PurgeChart(topic => TopicExcludeService.Matches(topic, filter)));

    private void OnPurgeExcludedSparkplug(SparkplugTopicExclude rule) =>
        MarkPurged(PurgeChart(topic =>
            TopicBranchMatcher.MatchesSparkplug(rule.Scope, rule.MessageType, topic)));

    private void MarkPurged(bool changed)
    {
        // Notification stays out of the purge itself: it runs while the exclude service
        // holds its locks. One coalesced flag is enough, and a purge whose observer never
        // runs cannot leave per-filter state behind to misfire on a later exclusion.
        if (changed)
            Interlocked.Exchange(ref _purgedNotify, 1);
    }

    private bool PurgeChart(Func<string, bool> predicate)
    {
        var changed = registry.RemoveMatchingTopics(predicate);
        foreach (var config in chartSettings.GetCharts(_connectionId))
        {
            foreach (var series in config.Series)
            {
                if (!predicate(series.Topic) || !_buffers.TryRemove(series.Id, out _))
                    continue;

                changed = true;
            }
        }

        return changed;
    }

    // Runs right after the matching purge on the same thread, so consuming the flag here
    // notifies only when that purge actually changed chart state.
    private void OnTopicExcluded(string filter) => NotifyIfPurged();

    private void OnSparkplugExcluded(SparkplugTopicExclude rule) => NotifyIfPurged();

    private void NotifyIfPurged()
    {
        if (Interlocked.Exchange(ref _purgedNotify, 0) != 0)
            OnDataUpdated?.Invoke();
    }

    private bool TryExtractFields(
        string? payload,
        IReadOnlyDictionary<ulong, string>? aliasNames,
        out IReadOnlyDictionary<string, ExtractedField> fields)
    {
        fields = new Dictionary<string, ExtractedField>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(payload))
            return false;

        try
        {
            fields = extractor.Extract(payload, aliasNames);
        }
        catch
        {
            return false;
        }

        return fields.Count > 0;
    }

    private void OnChartsChanged(Guid connectionId)
    {
        lock (_stateLock)
        {
            if (connectionId != _connectionId) return;
            _sources = ReconcileSources(connectionId);
            _sourcesInitialized = true;
        }

        OnDataUpdated?.Invoke();
    }

    private ImmutableDictionary<Guid, SeriesIdentity> ReconcileSources(Guid connectionId)
    {
        var builder = ImmutableDictionary.CreateBuilder<Guid, SeriesIdentity>();

        foreach (var config in chartSettings.GetCharts(connectionId))
        {
            foreach (var series in config.Series)
            {
                if (_sources.TryGetValue(series.Id, out var existing)
                    && string.Equals(existing.Topic, series.Topic, StringComparison.Ordinal)
                    && string.Equals(existing.JsonPath, series.JsonPath, StringComparison.Ordinal))
                {
                    builder[series.Id] = existing with { MaxPoints = config.MaxPoints };
                    if (_buffers.TryGetValue(series.Id, out var buffer))
                        TrimBuffer(buffer, config.MaxPoints);
                    continue;
                }

                if (existing is not null)
                    _buffers.TryRemove(series.Id, out _);

                builder[series.Id] = new SeriesIdentity(new object(), series.Topic, series.JsonPath, config.MaxPoints);
            }
        }

        foreach (var (seriesId, _) in _sources)
        {
            if (!builder.ContainsKey(seriesId))
                _buffers.TryRemove(seriesId, out _);
        }

        return builder.ToImmutable();
    }

    private ImmutableDictionary<Guid, SeriesIdentity> CaptureSources(Guid connectionId)
    {
        var builder = ImmutableDictionary.CreateBuilder<Guid, SeriesIdentity>();
        foreach (var config in chartSettings.GetCharts(connectionId))
        {
            foreach (var series in config.Series)
                builder[series.Id] = new SeriesIdentity(new object(), series.Topic, series.JsonPath, config.MaxPoints);
        }

        return builder.ToImmutable();
    }

    private HandlerState CaptureHandlerState()
    {
        lock (_stateLock)
        {
            if (!_sourcesInitialized)
            {
                _sources = CaptureSources(_connectionId);
                _sourcesInitialized = true;
            }

            return new HandlerState(_epoch, _connectionId, _sources);
        }
    }

    private readonly record struct HandlerState(
        long Epoch,
        Guid ConnectionId,
        ImmutableDictionary<Guid, SeriesIdentity> Sources);

    private sealed record SeriesIdentity(object Token, string Topic, string JsonPath, int MaxPoints);

    private bool UpdateBuffers(
        HandlerState state,
        string topic,
        IReadOnlyDictionary<string, ExtractedField> fields,
        DateTime timestamp)
    {
        var updated = false;
        foreach (var config in chartSettings.GetCharts(state.ConnectionId))
            updated |= UpdateBuffersForConfiguration(state, topic, fields, timestamp, config);

        return updated;
    }

    private bool UpdateBuffersForConfiguration(
        HandlerState state,
        string topic,
        IReadOnlyDictionary<string, ExtractedField> fields,
        DateTime timestamp,
        ChartConfiguration config)
    {
        var updated = false;

        foreach (var series in config.Series)
        {
            if (!string.Equals(series.Topic, topic, StringComparison.Ordinal))
                continue;

            if (!fields.TryGetValue(series.JsonPath, out var extracted))
                continue;

            var point = new ChartDataPoint(timestamp, extracted.Value);
            if (TryEnqueue(state, series.Id, series.Topic, series.JsonPath, point))
                updated = true;
        }

        return updated;
    }

    private static void TrimBuffer(ConcurrentQueue<ChartDataPoint> buffer, int maxPoints)
    {
        while (buffer.Count > maxPoints)
        {
            if (!buffer.TryDequeue(out _))
                break;
        }
    }

    private bool TryEnqueue(
        HandlerState state,
        Guid seriesId,
        string topic,
        string jsonPath,
        ChartDataPoint point)
    {
        lock (_stateLock)
        {
            if (state.Epoch != _epoch)
                return false;

            if (!state.Sources.TryGetValue(seriesId, out var captured))
                return false;

            if (!string.Equals(captured.Topic, topic, StringComparison.Ordinal)
                || !string.Equals(captured.JsonPath, jsonPath, StringComparison.Ordinal))
            {
                return false;
            }

            if (!_sources.TryGetValue(seriesId, out var current)
                || !ReferenceEquals(captured.Token, current.Token))
            {
                return false;
            }

            var buffer = _buffers.GetOrAdd(seriesId, _ => new ConcurrentQueue<ChartDataPoint>());

            buffer.Enqueue(point);
            TrimBuffer(buffer, current.MaxPoints);
            return true;
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing)
        {
            client.ApplicationMessageReceivedAsync -= MessageHandler;
            chartSettings.ChartsChanged -= OnChartsChanged;
            DetachExclusionHooks();

            IsListening = false;
            _gate.Dispose();
        }
        _disposed = true;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private bool _disposed;
}
