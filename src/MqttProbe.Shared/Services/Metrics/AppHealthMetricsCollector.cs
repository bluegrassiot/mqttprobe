using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace MqttProbe.Services.Metrics;

public sealed record AppHealthMetricsSnapshot(
    double? CpuUsagePercent,
    double? ManagedHeapMb,
    double? WorkingSetMb,
    int? ThreadCount,
    long? ThreadPoolQueueLength,
    int? GcGen2Collections,
    double? UptimeSeconds)
{
    public bool HasAny =>
        CpuUsagePercent.HasValue
        || ManagedHeapMb.HasValue
        || WorkingSetMb.HasValue
        || ThreadCount.HasValue
        || ThreadPoolQueueLength.HasValue
        || GcGen2Collections.HasValue
        || UptimeSeconds.HasValue;
}

public interface IAppHealthMetricsCollector : IDisposable
{
    public AppHealthMetricsSnapshot GetSnapshot();
}

public sealed class AppHealthMetricsCollector : IAppHealthMetricsCollector
{
    private readonly ILogger<AppHealthMetricsCollector> _logger;
    private readonly DateTime _startTime = DateTime.UtcNow;
    private readonly Func<Process, TimeSpan> _getCpuTime;
    private readonly Func<Process, long> _getWorkingSet64;
    private readonly Func<Process, int> _getThreadCount;
    private readonly Func<Process> _getCurrentProcess;
    private DateTime _lastCpuSample = DateTime.UtcNow;
    private TimeSpan _lastCpuTime;
    private readonly System.Timers.Timer _timer;
    private int _sampling;
    private volatile bool _disposed;

    private volatile bool _cpuAvailable;
    private volatile bool _workingSetAvailable;
    private volatile bool _threadsAvailable;

    private double _cpuUsagePercent;
    private double _managedHeapMb;
    private double _workingSetMb;
    private volatile int _threadCount;
    private long _threadPoolQueueLength;
    private volatile int _gcGen2Collections;
    private double _uptimeSeconds;

    public AppHealthMetricsCollector(ILogger<AppHealthMetricsCollector> logger,
        Func<Process, TimeSpan>? getCpuTime = null)
        : this(logger, getCpuTime, null, null)
    {
    }

    internal AppHealthMetricsCollector(
        ILogger<AppHealthMetricsCollector> logger,
        Func<Process, TimeSpan>? getCpuTime,
        Func<Process, long>? getWorkingSet64,
        Func<Process, int>? getThreadCount,
        Func<Process>? getCurrentProcess = null)
    {
        _logger = logger;
        _getCpuTime = getCpuTime ?? (p => p.TotalProcessorTime);
        _getWorkingSet64 = getWorkingSet64 ?? (p => p.WorkingSet64);
        _getThreadCount = getThreadCount ?? (p => p.Threads.Count);
        _getCurrentProcess = getCurrentProcess ?? Process.GetCurrentProcess;

        ProbeProcessMetrics();

        _timer = new System.Timers.Timer(1000);
        _timer.Elapsed += (_, _) =>
        {
            if (!_disposed) Sample();
        };
        _timer.AutoReset = true;
        _timer.Start();
        Sample();
    }

    private void ProbeProcessMetrics()
    {
        Process? process = null;
        try
        {
            process = _getCurrentProcess();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Process API unavailable; CPU, Working Set, and Thread Count metrics disabled");
            return;
        }

        using (process)
        {
            _cpuAvailable = ProbeCpu(process);
            _workingSetAvailable = Probe(() => _getWorkingSet64(process), "Working Set");
            _threadsAvailable = Probe(() => _getThreadCount(process), "Thread Count");
        }
    }

    private bool ProbeCpu(Process process)
    {
        try
        {
            _lastCpuTime = _getCpuTime(process);
            _lastCpuSample = DateTime.UtcNow;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "CPU usage metric unavailable on this platform");
            return false;
        }
    }

    private bool Probe(Func<object> probe, string metricName)
    {
        try
        {
            probe();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "{MetricName} metric unavailable on this platform", metricName);
            return false;
        }
    }

    public AppHealthMetricsSnapshot GetSnapshot() => new(
        CpuUsagePercent: _cpuAvailable ? Volatile.Read(ref _cpuUsagePercent) : null,
        ManagedHeapMb: Volatile.Read(ref _managedHeapMb),
        WorkingSetMb: _workingSetAvailable ? Volatile.Read(ref _workingSetMb) : null,
        ThreadCount: _threadsAvailable ? _threadCount : null,
        ThreadPoolQueueLength: Volatile.Read(ref _threadPoolQueueLength),
        GcGen2Collections: _gcGen2Collections,
        UptimeSeconds: Volatile.Read(ref _uptimeSeconds));

    private void Sample()
    {
        if (Interlocked.Exchange(ref _sampling, 1) == 1) return;
        try
        {
            var now = DateTime.UtcNow;

            if (_cpuAvailable || _workingSetAvailable || _threadsAvailable)
            {
                Process? process = null;
                try
                {
                    process = _getCurrentProcess();
                }
                catch (Exception ex)
                {
                    _cpuAvailable = false;
                    _workingSetAvailable = false;
                    _threadsAvailable = false;
                    _logger.LogDebug(ex, "Process API unavailable; process metrics disabled");
                }

                if (process is not null)
                {
                    try
                    {
                        process.Refresh();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Process refresh failed");
                    }

                    using (process)
                    {
                        if (_cpuAvailable)
                        {
                            try
                            {
                                var cpuTime = _getCpuTime(process);
                                var elapsedSec = (now - _lastCpuSample).TotalSeconds;
                                var cpuUsage = elapsedSec > 0
                                    ? Math.Min(100.0, (cpuTime - _lastCpuTime).TotalSeconds
                                        / (elapsedSec * Environment.ProcessorCount) * 100.0)
                                    : 0.0;
                                _lastCpuSample = now;
                                _lastCpuTime = cpuTime;
                                Volatile.Write(ref _cpuUsagePercent, cpuUsage);
                            }
                            catch (Exception ex)
                            {
                                _cpuAvailable = false;
                                _logger.LogDebug(ex, "CPU usage metric disabled mid-flight");
                            }
                        }

                        if (_workingSetAvailable)
                        {
                            try
                            {
                                Volatile.Write(ref _workingSetMb, _getWorkingSet64(process) / 1048576.0);
                            }
                            catch (Exception ex)
                            {
                                _workingSetAvailable = false;
                                _logger.LogDebug(ex, "Working Set metric disabled mid-flight");
                            }
                        }

                        if (_threadsAvailable)
                        {
                            try
                            {
                                _threadCount = _getThreadCount(process);
                            }
                            catch (Exception ex)
                            {
                                _threadsAvailable = false;
                                _logger.LogDebug(ex, "Thread Count metric disabled mid-flight");
                            }
                        }
                    }
                }
            }

            Volatile.Write(ref _managedHeapMb, GC.GetTotalMemory(false) / 1048576.0);
            Volatile.Write(ref _threadPoolQueueLength, ThreadPool.PendingWorkItemCount);
            _gcGen2Collections = GC.CollectionCount(2);
            Volatile.Write(ref _uptimeSeconds, (now - _startTime).TotalSeconds);
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
        _disposed = true;
        _timer.Stop();
        _timer.Dispose();
        while (Volatile.Read(ref _sampling) == 1)
        {
            Thread.Sleep(10);
        }
    }
}
