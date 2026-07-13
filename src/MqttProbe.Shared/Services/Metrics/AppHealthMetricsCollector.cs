using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace MqttProbe.Services.Metrics;

public sealed record AppHealthMetricsSnapshot(
    bool Available,
    double CpuUsagePercent,
    double ManagedHeapMb,
    double WorkingSetMb,
    int ThreadCount,
    long ThreadPoolQueueLength,
    int GcGen2Collections,
    double UptimeSeconds);

public interface IAppHealthMetricsCollector : IDisposable
{
    public AppHealthMetricsSnapshot GetSnapshot();
}

public sealed class AppHealthMetricsCollector : IAppHealthMetricsCollector
{
    private readonly ILogger<AppHealthMetricsCollector> _logger;
    private readonly DateTime _startTime = DateTime.UtcNow;
    private readonly Func<TimeSpan> _getCpuTime;
    private DateTime _lastCpuSample = DateTime.UtcNow;
    private TimeSpan _lastCpuTime;
    private volatile bool _available;
    private readonly System.Timers.Timer? _timer;
    private int _sampling;

    private double _cpuUsagePercent;
    private double _managedHeapMb;
    private double _workingSetMb;
    private volatile int _threadCount;
    private long _threadPoolQueueLength;
    private volatile int _gcGen2Collections;
    private double _uptimeSeconds;

    public AppHealthMetricsCollector(ILogger<AppHealthMetricsCollector> logger,
        Func<TimeSpan>? getCpuTime = null)
    {
        _logger = logger;
        _getCpuTime = getCpuTime ?? (() => Process.GetCurrentProcess().TotalProcessorTime);
        try
        {
            _lastCpuTime = _getCpuTime();
            _available = true;
        }
        catch (PlatformNotSupportedException)
        {
            _available = false;
            _logger.LogDebug("App health telemetry unavailable on this platform");
        }

        if (_available)
        {
            _timer = new System.Timers.Timer(1000);
            _timer.Elapsed += (_, _) => Sample();
            _timer.AutoReset = true;
            _timer.Start();
            Sample();
        }
    }

    public AppHealthMetricsSnapshot GetSnapshot() => new(
        Available: _available,
        CpuUsagePercent: Volatile.Read(ref _cpuUsagePercent),
        ManagedHeapMb: Volatile.Read(ref _managedHeapMb),
        WorkingSetMb: Volatile.Read(ref _workingSetMb),
        ThreadCount: _threadCount,
        ThreadPoolQueueLength: Volatile.Read(ref _threadPoolQueueLength),
        GcGen2Collections: _gcGen2Collections,
        UptimeSeconds: Volatile.Read(ref _uptimeSeconds));

    private void Sample()
    {
        if (!_available) return;
        if (Interlocked.Exchange(ref _sampling, 1) == 1) return;
        try
        {
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            var now = DateTime.UtcNow;
            var cpuTime = _getCpuTime();
            var elapsedSec = (now - _lastCpuSample).TotalSeconds;
            var cpuUsage = elapsedSec > 0
                ? Math.Min(100.0, (cpuTime - _lastCpuTime).TotalSeconds
                    / (elapsedSec * Environment.ProcessorCount) * 100.0)
                : 0.0;
            _lastCpuSample = now;
            _lastCpuTime = cpuTime;

            Volatile.Write(ref _cpuUsagePercent, cpuUsage);
            Volatile.Write(ref _managedHeapMb, GC.GetTotalMemory(false) / 1048576.0);
            Volatile.Write(ref _workingSetMb, process.WorkingSet64 / 1048576.0);
            _threadCount = process.Threads.Count;
            Volatile.Write(ref _threadPoolQueueLength, ThreadPool.PendingWorkItemCount);
            _gcGen2Collections = GC.CollectionCount(2);
            Volatile.Write(ref _uptimeSeconds, (now - _startTime).TotalSeconds);
        }
        catch (PlatformNotSupportedException ex)
        {
            _available = false;
            _timer?.Stop();
            _logger.LogDebug(ex, "App health telemetry unavailable on this platform");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to sample app health");
        }
        finally
        {
            Interlocked.Exchange(ref _sampling, 0);
        }
    }

    public void Dispose()
    {
        _timer?.Stop();
        _timer?.Dispose();
        while (Volatile.Read(ref _sampling) == 1)
        {
            Thread.Sleep(10);
        }
    }
}
