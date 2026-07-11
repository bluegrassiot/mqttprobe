using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;
using MqttProbe.Models.Chart;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Sparkplug;

namespace MqttProbe.Services.Chart;

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
    IManagedMqttClient client,
    IPayloadDecoder payloadDecoder,
    IJsonFieldExtractor extractor,
    IChartFieldRegistry registry,
    ISettingsStore settingsStore,
    ILogger<ChartDataService>? logger = null)
    : IChartDataService
{
    private readonly ConcurrentDictionary<Guid, ConcurrentQueue<ChartDataPoint>> _buffers = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Guid _connectionId;

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
            settingsStore.ChartsChanged += OnChartsChanged;
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
            settingsStore.ChartsChanged -= OnChartsChanged;
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
            var decoded = payloadDecoder.Decode(e);
            var payload = decoded.Payload;
            if (!TryExtractFields(payload, decoded.AliasNames, out var fields))
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

    private bool TryExtractFields(
        string? payload,
        IReadOnlyDictionary<ulong, string>? aliasNames,
        out IReadOnlyDictionary<string, ExtractedField> fields)
    {
        fields = new Dictionary<string, ExtractedField>();
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
        settingsStore.GetCharts(_connectionId).Any(config => UpdateBuffersForConfiguration(topic, fields, timestamp, config));

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
            settingsStore.ChartsChanged -= OnChartsChanged;
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
