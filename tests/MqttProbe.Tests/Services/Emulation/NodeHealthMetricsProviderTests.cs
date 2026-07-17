using Microsoft.Extensions.Logging;
using MqttProbe.Services.Emulation;
using MqttProbe.Services.Metrics;
using SparkplugNet.Core.Enumerations;
using SparkplugNet.VersionB.Data;

namespace MqttProbe.Shared.Tests.Services.Emulation;

[TestFixture]
public class NodeHealthMetricsProviderTests
{
    [Test]
    public void BuildSnapshot_WhenAllAvailable_IncludesAllProcessMetrics()
    {
        using var collector = new AppHealthMetricsCollector(
            Substitute.For<ILogger<AppHealthMetricsCollector>>());
        var provider = new NodeHealthMetricsProvider(collector);

        var metrics = provider.BuildSnapshot(publishersOnline: 1, publishCycles: 10);

        var names = metrics.Select(m => m.Name).ToList();
        names.Should().Contain("CPU Usage (%)");
        names.Should().Contain("Managed Heap (MB)");
        names.Should().Contain("Working Set (MB)");
        names.Should().Contain("Thread Count");
        names.Should().Contain("ThreadPool Queue");
        names.Should().Contain("GC Gen2 Collections");
        names.Should().Contain("Uptime (s)");
        names.Should().Contain("Publishers Online");
        names.Should().Contain("Publish Cycles");
        metrics.Count.Should().Be(9);
    }

    [Test]
    public void BuildSnapshot_WhenProcessUnavailable_OmitsOnlyProcessMetrics()
    {
        using var collector = new AppHealthMetricsCollector(
            Substitute.For<ILogger<AppHealthMetricsCollector>>(),
            getCpuTime: static _ => throw new PlatformNotSupportedException(),
            getWorkingSet64: static _ => throw new PlatformNotSupportedException(),
            getThreadCount: static _ => throw new PlatformNotSupportedException());
        var provider = new NodeHealthMetricsProvider(collector);

        var metrics = provider.BuildSnapshot(publishersOnline: 1, publishCycles: 10);

        var names = metrics.Select(m => m.Name).ToList();
        names.Should().NotContain("CPU Usage (%)");
        names.Should().Contain("Managed Heap (MB)");
        names.Should().NotContain("Working Set (MB)");
        names.Should().NotContain("Thread Count");
        names.Should().Contain("ThreadPool Queue");
        names.Should().Contain("GC Gen2 Collections");
        names.Should().Contain("Uptime (s)");
        names.Should().Contain("Publishers Online");
        names.Should().Contain("Publish Cycles");
        metrics.Count.Should().Be(6);
    }

    [Test]
    public void BuildSnapshot_WhenProcessUnavailable_KeepsEmulationMetrics()
    {
        using var collector = new AppHealthMetricsCollector(
            Substitute.For<ILogger<AppHealthMetricsCollector>>(),
            getCpuTime: static _ => throw new PlatformNotSupportedException());
        var provider = new NodeHealthMetricsProvider(collector);

        var metrics = provider.BuildSnapshot(publishersOnline: 3, publishCycles: 42);

        var names = metrics.Select(m => m.Name).ToList();
        names.Should().Contain("Publishers Online");
        names.Should().Contain("Publish Cycles");
        metrics.First(m => m.Name == "Publishers Online").Value.Should().Be(3.0);
        metrics.First(m => m.Name == "Publish Cycles").Value.Should().Be(42.0);
    }
}
