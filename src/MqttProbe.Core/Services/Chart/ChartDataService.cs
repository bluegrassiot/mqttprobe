using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MqttProbe.Core.Models.Chart;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Core.Services.Plugins.Pipeline;
using MqttProbe.Core.Services.Sparkplug;
using MqttProbe.PluginContracts;

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
    private Guid _connectionId;
    private int _exclusionHooks;
    private int _purgedNotify;
    private readonly ITopicExcludeService? _topicExcludeService = topicExcludeService;

    public event Action? OnDataUpdated;
    public bool IsListening { get; private set; }

    public void SetConnection(Guid connectionId)
    {
        _connectionId = connectionId;
        _buffers.Clear();
    }

    public void ClearBuffers()
    {
        _buffers.Clear();
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
            client.ApplicationMessageReceivedAsync += MessageHandler;
            chartSettings.ChartsChanged += OnChartsChanged;
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
    }

    private void DetachExclusionHooks()
    {
        if (_topicExcludeService is null)
            return;

        if (Interlocked.Exchange(ref _exclusionHooks, 0) == 0)
            return;

        _topicExcludeService.PurgeExcludedTopic -= OnPurgeExcludedTopic;
        _topicExcludeService.TopicExcluded -= OnTopicExcluded;
    }

    private Task MessageHandler(MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;
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
                var rawPayload = e.ApplicationMessage.GetPayloadSegment().Count > 0
                    ? e.ApplicationMessage.GetPayloadSegment().ToArray()
                    : [];
                aliasNames = SparkplugAliasResolver.Resolve(topic, rawPayload, topologyService.Groups);
            }

            if (!TryExtractFields(payload, aliasNames, out var fields))
                return Task.CompletedTask;

            registry.Update(topic, fields);

            var updated = UpdateBuffers(topic, fields, DateTime.UtcNow);

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

    private void OnPurgeExcludedTopic(string filter)
    {
        var changed = PurgeChart(filter);

        // Notification stays out of the purge itself: it runs while the exclude service
        // holds its locks. One coalesced flag is enough, and a purge whose observer never
        // runs cannot leave per-filter state behind to misfire on a later exclusion.
        if (changed)
            Interlocked.Exchange(ref _purgedNotify, 1);
    }

    private bool PurgeChart(string filter)
    {
        var changed = registry.RemoveMatchingTopic(filter);
        foreach (var config in chartSettings.GetCharts(_connectionId))
        {
            foreach (var series in config.Series)
            {
                if (!TopicExcludeService.Matches(series.Topic, filter)
                    || !_buffers.TryRemove(series.Id, out _))
                    continue;

                changed = true;
            }
        }

        return changed;
    }

    // Runs right after the matching purge on the same thread, so consuming the flag here
    // notifies only when that purge actually changed chart state.
    private void OnTopicExcluded(string filter)
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
        if (connectionId != _connectionId) return;
        _buffers.Clear();
        OnDataUpdated?.Invoke();
    }

    private bool UpdateBuffers(string topic, IReadOnlyDictionary<string, ExtractedField> fields, DateTime timestamp) =>
        chartSettings.GetCharts(_connectionId).Any(config => UpdateBuffersForConfiguration(topic, fields, timestamp, config));

    private bool UpdateBuffersForConfiguration(
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

            EnqueuePoint(series.Id, config.MaxPoints, new ChartDataPoint(timestamp, extracted.Value));
            updated = true;
        }

        return updated;
    }

    private void EnqueuePoint(Guid seriesId, int maxPoints, ChartDataPoint point)
    {
        var buffer = _buffers.GetOrAdd(seriesId, _ => new ConcurrentQueue<ChartDataPoint>());

        while (buffer.Count >= maxPoints)
            buffer.TryDequeue(out _);

        buffer.Enqueue(point);
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
