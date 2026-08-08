using System.Threading.RateLimiting;
using Microsoft.Extensions.Logging;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Metrics;

namespace MqttProbe.Services.Mqtt;

internal sealed class InboundRateLimiter(
    IPerformanceSettings performanceSettings, IUxMetricsService metrics, ILogger logger) : IDisposable
{
    private readonly Lock _sync = new();
    private FixedWindowRateLimiter _limiter = Build(performanceSettings.Performance.MaxMessagesPerSecond);
    private bool _limitLogged;
    private bool _disposed;
    private long _droppedCount;

    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    public bool TryAcquire()
    {
        bool acquired;
        lock (_sync)
        {
            // A message already in flight can reach here after Dispose. Retire it quietly
            // rather than let ObjectDisposedException escape into the MQTT client's handler,
            // which invokes us outside its own try block.
            if (_disposed) return false;

            // Released immediately: a fixed window replenishes on its timer, never on lease
            // disposal, so holding the lease for the message's lifetime would buy nothing.
            using var lease = _limiter.AttemptAcquire();
            acquired = lease.IsAcquired;
        }

        if (acquired)
        {
            _limitLogged = false;
            return true;
        }

        Interlocked.Increment(ref _droppedCount);
        metrics.RecordMessageDropped();
        LogLimitOnce();
        return false;
    }

    public void Rebuild()
    {
        var replacement = Build(performanceSettings.Performance.MaxMessagesPerSecond);

        FixedWindowRateLimiter previous;
        lock (_sync)
        {
            // Settings changes fan out to every subscriber and are awaited by the settings
            // UI, so a rebuild racing disposal must retire quietly rather than throw.
            if (_disposed)
            {
                replacement.Dispose();
                return;
            }

            previous = _limiter;
            _limiter = replacement;
        }

        try { previous.Dispose(); }
        catch (Exception ex) { logger.LogWarning(ex, "Failed to dispose old rate limiter; new limiter is already in place."); }
    }

    public void Reset()
    {
        _limitLogged = false;
        Interlocked.Exchange(ref _droppedCount, 0);
    }

    public void Dispose()
    {
        // Disposed under the lock so an in-flight TryAcquire cannot be mid-AttemptAcquire
        // on the limiter being torn down.
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _limiter.Dispose();
        }
    }

    private static FixedWindowRateLimiter Build(int permitsPerSecond) =>
        new(new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitsPerSecond,
            Window = TimeSpan.FromSeconds(1),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0
        });

    private void LogLimitOnce()
    {
        if (_limitLogged) return;
        _limitLogged = true;
        logger.LogWarning(
            "Inbound message rate limit ({Limit}/s) exceeded; messages are being dropped. " +
            "Increase MaxMessagesPerSecond in PerformanceSettings if needed.",
            performanceSettings.Performance.MaxMessagesPerSecond);
    }
}
