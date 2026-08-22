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
            if (_topicExcludeService is not null)
                _topicExcludeService.TopicExcluded += OnTopicExcluded;
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
            if (_topicExcludeService is not null)
                _topicExcludeService.TopicExcluded -= OnTopicExcluded;
            IsListening = false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task MessageHandler(MqttApplicationMessageReceivedEventArgs e)
    {
        try
        {
            var topic = e.ApplicationMessage.Topic;
            if (_topicExcludeService?.IsExcluded(topic) == true)
                return Task.CompletedTask;

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

            if (updated) OnDataUpdated?.Invoke();
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Error processing chart data message on topic {Topic}", e.ApplicationMessage.Topic);
        }

        return Task.CompletedTask;
    }

    private void OnTopicExcluded(string filter)
    {
        var registryChanged = registry.RemoveMatchingTopic(filter);
        var buffersChanged = false;
        foreach (var config in chartSettings.GetCharts(_connectionId))
        {
            foreach (var series in config.Series)
            {
                if (!TopicExcludeService.Matches(series.Topic, filter)
                    || !_buffers.TryRemove(series.Id, out _))
                    continue;

                buffersChanged = true;
            }
        }

        if (registryChanged || buffersChanged)
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
            if (_topicExcludeService is not null)
                _topicExcludeService.TopicExcluded -= OnTopicExcluded;
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
