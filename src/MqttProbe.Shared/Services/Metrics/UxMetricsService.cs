using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using MqttProbe.Services.Configuration;

namespace MqttProbe.Services.Metrics;

public sealed record UxMetricsSnapshot(
    long ConnectAttempts,
    long ConnectSuccesses,
    long ConnectFailures,
    long PublishSuccesses,
    long PublishFailures,
    long ChartsCreated,
    long SeriesAddedToExistingCharts,
    long MessagesProcessed,
    long MessagesDropped,
    double AvgProcessingTimeUs,
    double MaxProcessingTimeUs,
    double AvgPayloadBytes,
    long MaxPayloadBytes,
    int CurrentMessagesPerSecond,
    IReadOnlyList<int> MessageRateHistory,
    IReadOnlyDictionary<string, long> MessagesProcessedByFormat,
    IReadOnlyDictionary<string, long> ChartFunnelBySource,
    // Display-limit scope
    int MaxDisplayMessages,
    int CurrentDisplayedMessageCount,
    // App health scope (delegated to IAppHealthMetricsCollector)
    AppHealthMetricsSnapshot AppHealth,
    // Emulation scope
    int EmulatorPublishersOnline,
    long EmulatorPublishCycles,
    int EmulatorNodesInError);

public interface IUxMetricsService
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
    public UxMetricsSnapshot GetSnapshot();
    public void SetDisplayedMessageCount(int count);
    public void UpdateEmulatorHealth(int publishersOnline, long publishCycles, int nodesInError);
    public void ClearEmulatorHealth();
}

public sealed class UxMetricsService : IUxMetricsService
{
    public const int RateWindowSeconds = 60;

    private readonly ILogger<UxMetricsService> _logger;
    private readonly ISettingsStore _settingsStore;
    private readonly Func<long> _tickCount64Ms;
    private readonly IAppHealthMetricsCollector _healthCollector;
    private int _displayedCount;

    // Emulation metrics (written by EmulationService)
    private volatile int _emulatorPublishersOnline;
    private long _emulatorPublishCycles;
    private volatile int _emulatorNodesInError;

    private long _connectAttempts;
    private long _connectSuccesses;
    private long _connectFailures;
    private long _publishSuccesses;
    private long _publishFailures;
    private long _chartsCreated;
    private long _seriesAddedToExistingCharts;
    private readonly ConcurrentDictionary<string, long> _chartFunnelBySource = new(StringComparer.OrdinalIgnoreCase);

    private long _messagesProcessed;
    private long _messagesDropped;
    private readonly ConcurrentDictionary<string, long> _messagesProcessedByFormat = new(StringComparer.OrdinalIgnoreCase);

    // Guards the running aggregates and the rate ring below.
    private readonly Lock _statsSync = new();
    private long _processingCount;
    private double _processingSumUs;
    private double _processingMaxUs;
    private long _payloadCount;
    private long _payloadSumBytes;
    private long _payloadMaxBytes;

    // Ring of one-second buckets; index = second % RateWindowSeconds. A bucket is
    // valid only if its stamp matches the second being read, so stale buckets read as 0.
    private readonly int[] _rateBuckets = new int[RateWindowSeconds];
    private readonly long[] _rateBucketSeconds = new long[RateWindowSeconds];

    public UxMetricsService(ILogger<UxMetricsService> logger, ISettingsStore settingsStore,
        IAppHealthMetricsCollector healthCollector)
        : this(logger, settingsStore, healthCollector, static () => Environment.TickCount64)
    {
    }

    internal UxMetricsService(
        ILogger<UxMetricsService> logger,
        ISettingsStore settingsStore,
        IAppHealthMetricsCollector healthCollector,
        Func<long> tickCount64Ms)
    {
        _logger = logger;
        _settingsStore = settingsStore;
        _healthCollector = healthCollector;
        _tickCount64Ms = tickCount64Ms;
        Array.Fill(_rateBucketSeconds, -1);
    }

    public void RecordConnectAttempt()
    {
        Interlocked.Increment(ref _connectAttempts);
        _logger.LogInformation("UX metrics: connect attempt");
    }

    public void RecordConnectSuccess()
    {
        Interlocked.Increment(ref _connectSuccesses);
        _logger.LogInformation("UX metrics: connect success");
    }

    public void RecordConnectFailure()
    {
        Interlocked.Increment(ref _connectFailures);
        _logger.LogWarning("UX metrics: connect failure");
    }

    public void RecordPublishOutcome(bool success)
    {
        if (success)
        {
            Interlocked.Increment(ref _publishSuccesses);
            _logger.LogDebug("UX metrics: publish success");
        }
        else
        {
            Interlocked.Increment(ref _publishFailures);
            _logger.LogDebug("UX metrics: publish failure");
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
            _logger.LogInformation("UX metrics: chart funnel source={Source} newChart={CreatedNewChart}",
                source, createdNewChart);
    }

    public void RecordMessageProcessed(string format)
    {
        Interlocked.Increment(ref _messagesProcessed);
        _messagesProcessedByFormat.AddOrUpdate(format, 1, (_, count) => count + 1);

        lock (_statsSync)
        {
            var second = _tickCount64Ms() / 1000;
            var index = (int)(second % RateWindowSeconds);
            if (_rateBucketSeconds[index] != second)
            {
                _rateBucketSeconds[index] = second;
                _rateBuckets[index] = 0;
            }
            _rateBuckets[index]++;
        }
    }

    public void RecordMessageDropped() => Interlocked.Increment(ref _messagesDropped);

    public void RecordProcessingTime(double microseconds)
    {
        lock (_statsSync)
        {
            _processingCount++;
            _processingSumUs += microseconds;
            if (microseconds > _processingMaxUs)
                _processingMaxUs = microseconds;
        }
    }

    public void RecordPayloadSize(long bytes)
    {
        lock (_statsSync)
        {
            _payloadCount++;
            _payloadSumBytes += bytes;
            if (bytes > _payloadMaxBytes)
                _payloadMaxBytes = bytes;
        }
    }

    public void SetDisplayedMessageCount(int count)
    {
        Interlocked.Exchange(ref _displayedCount, count);
    }

    public void UpdateEmulatorHealth(int publishersOnline, long publishCycles, int nodesInError)
    {
        _emulatorPublishersOnline = publishersOnline;
        Volatile.Write(ref _emulatorPublishCycles, publishCycles);
        _emulatorNodesInError = nodesInError;
    }

    public void ClearEmulatorHealth()
    {
        _emulatorPublishersOnline = 0;
        Volatile.Write(ref _emulatorPublishCycles, 0L);
        _emulatorNodesInError = 0;
    }

    public UxMetricsSnapshot GetSnapshot()
    {
        double avgProcessingUs;
        double maxProcessingUs;
        double avgPayloadBytes;
        long maxPayloadBytes;
        var history = new int[RateWindowSeconds];

        lock (_statsSync)
        {
            avgProcessingUs = _processingCount == 0 ? 0 : _processingSumUs / _processingCount;
            maxProcessingUs = _processingMaxUs;
            avgPayloadBytes = _payloadCount == 0 ? 0 : (double)_payloadSumBytes / _payloadCount;
            maxPayloadBytes = _payloadMaxBytes;

            var nowSecond = _tickCount64Ms() / 1000;
            for (var i = 0; i < RateWindowSeconds; i++)
            {
                // Window of the last RateWindowSeconds *full* seconds, oldest first.
                var second = nowSecond - RateWindowSeconds + i;
                var index = (int)(second % RateWindowSeconds);
                history[i] = second >= 0 && _rateBucketSeconds[index] == second
                    ? _rateBuckets[index]
                    : 0;
            }
        }

        var health = _healthCollector.GetSnapshot();
        return new UxMetricsSnapshot(
            ConnectAttempts: Interlocked.Read(ref _connectAttempts),
            ConnectSuccesses: Interlocked.Read(ref _connectSuccesses),
            ConnectFailures: Interlocked.Read(ref _connectFailures),
            PublishSuccesses: Interlocked.Read(ref _publishSuccesses),
            PublishFailures: Interlocked.Read(ref _publishFailures),
            ChartsCreated: Interlocked.Read(ref _chartsCreated),
            SeriesAddedToExistingCharts: Interlocked.Read(ref _seriesAddedToExistingCharts),
            MessagesProcessed: Interlocked.Read(ref _messagesProcessed),
            MessagesDropped: Interlocked.Read(ref _messagesDropped),
            AvgProcessingTimeUs: avgProcessingUs,
            MaxProcessingTimeUs: maxProcessingUs,
            AvgPayloadBytes: avgPayloadBytes,
            MaxPayloadBytes: maxPayloadBytes,
            CurrentMessagesPerSecond: history[RateWindowSeconds - 1],
            MessageRateHistory: history,
            MessagesProcessedByFormat: new Dictionary<string, long>(_messagesProcessedByFormat),
            ChartFunnelBySource: new Dictionary<string, long>(_chartFunnelBySource),
            MaxDisplayMessages: _settingsStore.Config.Performance.MaxDisplayMessages,
            CurrentDisplayedMessageCount: Volatile.Read(ref _displayedCount),
            AppHealth: health,
            EmulatorPublishersOnline: _emulatorPublishersOnline,
            EmulatorPublishCycles: Volatile.Read(ref _emulatorPublishCycles),
            EmulatorNodesInError: _emulatorNodesInError);
    }

}
