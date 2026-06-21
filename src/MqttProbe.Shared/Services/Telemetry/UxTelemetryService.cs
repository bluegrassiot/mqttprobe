using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace MqttProbe.Services.Telemetry;

public sealed record UxTelemetrySnapshot(
    long ConnectAttempts,
    long ConnectSuccesses,
    long ConnectFailures,
    long PublishSuccesses,
    long PublishFailures,
    long ChartsCreated,
    long SeriesAddedToExistingCharts,
    IReadOnlyDictionary<string, long> ChartFunnelBySource);

public interface IUxTelemetryService : IDisposable
{
    public void RecordConnectAttempt();
    public void RecordConnectSuccess();
    public void RecordConnectFailure();
    public void RecordPublishOutcome(bool success);
    public void RecordChartFunnel(string source, bool createdNewChart);
    public void RecordMessageProcessed(string format);
    public void RecordMessageDropped();
    public void RecordProcessingTime(double microseconds);
    public void RecordPayloadSize(long bytes);
    public UxTelemetrySnapshot GetSnapshot();
}

public sealed class UxTelemetryService : IUxTelemetryService
{
    private readonly ILogger<UxTelemetryService> _logger;
    private long _connectAttempts;
    private long _connectSuccesses;
    private long _connectFailures;
    private long _publishSuccesses;
    private long _publishFailures;
    private long _chartsCreated;
    private long _seriesAddedToExistingCharts;
    private readonly ConcurrentDictionary<string, long> _chartFunnelBySource = new(StringComparer.OrdinalIgnoreCase);

    private readonly Meter _meter = new("MqttProbe", "1.0.0");
    private readonly Counter<long> _messagesProcessed;
    private readonly Counter<long> _messagesDropped;
    private readonly Histogram<double> _processingTime;
    private readonly Histogram<long> _payloadSize;

    public UxTelemetryService(ILogger<UxTelemetryService> logger)
    {
        _logger = logger;
        _messagesProcessed = _meter.CreateCounter<long>("mqttprobe.messages.processed", "messages", "Total messages processed");
        _messagesDropped = _meter.CreateCounter<long>("mqttprobe.messages.dropped", "messages", "Messages dropped by rate limiter");
        _processingTime = _meter.CreateHistogram<double>("mqttprobe.message.processing_time", "us", "Per-message processing time in microseconds");
        _payloadSize = _meter.CreateHistogram<long>("mqttprobe.message.payload_size", "bytes", "Payload size in bytes");
    }

    public void RecordConnectAttempt()
    {
        Interlocked.Increment(ref _connectAttempts);
        _logger.LogInformation("UX telemetry: connect attempt");
    }

    public void RecordConnectSuccess()
    {
        Interlocked.Increment(ref _connectSuccesses);
        _logger.LogInformation("UX telemetry: connect success");
    }

    public void RecordConnectFailure()
    {
        Interlocked.Increment(ref _connectFailures);
        _logger.LogWarning("UX telemetry: connect failure");
    }

    public void RecordPublishOutcome(bool success)
    {
        if (success)
        {
            Interlocked.Increment(ref _publishSuccesses);
            _logger.LogDebug("UX telemetry: publish success");
        }
        else
        {
            Interlocked.Increment(ref _publishFailures);
            _logger.LogDebug("UX telemetry: publish failure");
        }
    }

    public void RecordChartFunnel(string source, bool createdNewChart)
    {
        if (createdNewChart)
            Interlocked.Increment(ref _chartsCreated);
        else
            Interlocked.Increment(ref _seriesAddedToExistingCharts);

        _chartFunnelBySource.AddOrUpdate(source, 1, (_, count) => count + 1);
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("UX telemetry: chart funnel source={Source} newChart={CreatedNewChart}",
                source, createdNewChart);
    }

    public void RecordMessageProcessed(string format)
    {
        _messagesProcessed.Add(1,
            new KeyValuePair<string, object?>("format", format));
    }

    public void RecordMessageDropped() => _messagesDropped.Add(1);

    public void RecordProcessingTime(double microseconds) => _processingTime.Record(microseconds);

    public void RecordPayloadSize(long bytes) => _payloadSize.Record(bytes);

    public UxTelemetrySnapshot GetSnapshot() => new(
        ConnectAttempts: Interlocked.Read(ref _connectAttempts),
        ConnectSuccesses: Interlocked.Read(ref _connectSuccesses),
        ConnectFailures: Interlocked.Read(ref _connectFailures),
        PublishSuccesses: Interlocked.Read(ref _publishSuccesses),
        PublishFailures: Interlocked.Read(ref _publishFailures),
        ChartsCreated: Interlocked.Read(ref _chartsCreated),
        SeriesAddedToExistingCharts: Interlocked.Read(ref _seriesAddedToExistingCharts),
        ChartFunnelBySource: new Dictionary<string, long>(_chartFunnelBySource));

    public void Dispose() => _meter.Dispose();
}
