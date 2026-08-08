using MqttProbe.Services.Metrics;
using SparkplugNet.VersionB.Data;

namespace MqttProbe.Services.Emulation;

public class NodeHealthMetricsProvider(IAppHealthMetricsCollector collector)
{
    public IReadOnlyList<Metric> BuildSnapshot(int publishersOnline, long publishCycles)
    {
        var health = collector.GetSnapshot();
        var metrics = new List<Metric>();
        if (health.CpuUsagePercent.HasValue)
            metrics.Add(new Metric("CPU Usage (%)", DataType.Double, health.CpuUsagePercent.Value));
        if (health.ManagedHeapMb.HasValue)
            metrics.Add(new Metric("Managed Heap (MB)", DataType.Double, health.ManagedHeapMb.Value));
        if (health.WorkingSetMb.HasValue)
            metrics.Add(new Metric("Working Set (MB)", DataType.Double, health.WorkingSetMb.Value));
        if (health.ThreadCount.HasValue)
            metrics.Add(new Metric("Thread Count", DataType.Double, (double)health.ThreadCount.Value));
        if (health.ThreadPoolQueueLength.HasValue)
            metrics.Add(new Metric("ThreadPool Queue", DataType.Double, (double)health.ThreadPoolQueueLength.Value));
        if (health.GcGen2Collections.HasValue)
            metrics.Add(new Metric("GC Gen2 Collections", DataType.Double, (double)health.GcGen2Collections.Value));
        if (health.UptimeSeconds.HasValue)
            metrics.Add(new Metric("Uptime (s)", DataType.Double, health.UptimeSeconds.Value));
        metrics.Add(new Metric("Publishers Online", DataType.Double, (double)publishersOnline));
        metrics.Add(new Metric("Publish Cycles", DataType.Double, (double)publishCycles));
        return metrics;
    }
}
