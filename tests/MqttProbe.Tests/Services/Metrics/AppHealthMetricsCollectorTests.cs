using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.Logging;
using MqttProbe.Services.Metrics;

namespace MqttProbe.Shared.Tests.Services.Metrics;

[TestFixture]
public class AppHealthMetricsCollectorTests
{
    [Test]
    public void GetSnapshot_WhenAllAvailable_ReturnsAllNonNull()
    {
        using var collector = new AppHealthMetricsCollector(
            Substitute.For<ILogger<AppHealthMetricsCollector>>());

        var snapshot = collector.GetSnapshot();

        snapshot.HasAny.Should().BeTrue();
        snapshot.CpuUsagePercent.Should().NotBeNull();
        snapshot.ManagedHeapMb.Should().NotBeNull();
        snapshot.WorkingSetMb.Should().NotBeNull();
        snapshot.ThreadCount.Should().NotBeNull();
        snapshot.ThreadPoolQueueLength.Should().NotBeNull();
        snapshot.GcGen2Collections.Should().NotBeNull();
        snapshot.UptimeSeconds.Should().NotBeNull();
    }

    [Test]
    public void GetSnapshot_WhenProcessUnavailable_ReturnsProcessMetricsNull()
    {
        using var collector = new AppHealthMetricsCollector(
            Substitute.For<ILogger<AppHealthMetricsCollector>>(),
            getCpuTime: static _ => throw new PlatformNotSupportedException(),
            getWorkingSet64: static _ => throw new PlatformNotSupportedException(),
            getThreadCount: static _ => throw new PlatformNotSupportedException());

        var snapshot = collector.GetSnapshot();

        snapshot.HasAny.Should().BeTrue();
        snapshot.CpuUsagePercent.Should().BeNull();
        snapshot.ManagedHeapMb.Should().NotBeNull();
        snapshot.WorkingSetMb.Should().BeNull();
        snapshot.ThreadCount.Should().BeNull();
        snapshot.ThreadPoolQueueLength.Should().NotBeNull();
        snapshot.GcGen2Collections.Should().NotBeNull();
        snapshot.UptimeSeconds.Should().NotBeNull();
    }

    [Test]
    public void GetSnapshot_WhenCpuOnlyUnavailable_ReturnsCpuNullOthersNonNull()
    {
        using var collector = new AppHealthMetricsCollector(
            Substitute.For<ILogger<AppHealthMetricsCollector>>(),
            getCpuTime: static _ => throw new PlatformNotSupportedException());

        var snapshot = collector.GetSnapshot();

        snapshot.HasAny.Should().BeTrue();
        snapshot.CpuUsagePercent.Should().BeNull();
        snapshot.ManagedHeapMb.Should().NotBeNull();
        snapshot.WorkingSetMb.Should().NotBeNull();
        snapshot.ThreadCount.Should().NotBeNull();
        snapshot.ThreadPoolQueueLength.Should().NotBeNull();
        snapshot.GcGen2Collections.Should().NotBeNull();
        snapshot.UptimeSeconds.Should().NotBeNull();
    }

    [Test]
    public void Constructor_WhenProcessUnavailable_StartsTimerAndSamplesNonProcessMetrics()
    {
        using var collector = new AppHealthMetricsCollector(
            Substitute.For<ILogger<AppHealthMetricsCollector>>(),
            getCpuTime: static _ => throw new PlatformNotSupportedException(),
            getWorkingSet64: static _ => throw new PlatformNotSupportedException(),
            getThreadCount: static _ => throw new PlatformNotSupportedException());

        Thread.Sleep(150);

        var snapshot = collector.GetSnapshot();
        snapshot.HasAny.Should().BeTrue();
        snapshot.ManagedHeapMb.Should().NotBeNull();
        snapshot.ThreadPoolQueueLength.Should().NotBeNull();
        snapshot.GcGen2Collections.Should().NotBeNull();
        snapshot.UptimeSeconds.Should().BeGreaterThan(0);
        snapshot.CpuUsagePercent.Should().BeNull();
        snapshot.WorkingSetMb.Should().BeNull();
        snapshot.ThreadCount.Should().BeNull();
    }

    [Test]
    public void Constructor_WhenProcessAccessThrows_DoesNotThrow()
    {
        var act = () => new AppHealthMetricsCollector(
            Substitute.For<ILogger<AppHealthMetricsCollector>>(),
            getCpuTime: static _ => throw new PlatformNotSupportedException(),
            getWorkingSet64: static _ => throw new PlatformNotSupportedException(),
            getThreadCount: static _ => throw new PlatformNotSupportedException());

        act.Should().NotThrow();
    }

    [Test]
    public void Constructor_WhenProcessUnavailable_LogsDebugMessage()
    {
        var logger = Substitute.For<ILogger<AppHealthMetricsCollector>>();
        using var collector = new AppHealthMetricsCollector(
            logger,
            getCpuTime: static _ => throw new PlatformNotSupportedException(),
            getWorkingSet64: static _ => throw new PlatformNotSupportedException(),
            getThreadCount: static _ => throw new PlatformNotSupportedException());

        logger.Received().Log(
            LogLevel.Debug,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Test]
    public void Dispose_StopsTimer()
    {
        var collector = new AppHealthMetricsCollector(
            Substitute.For<ILogger<AppHealthMetricsCollector>>());

        collector.Dispose();

        var act = () => collector.GetSnapshot();
        act.Should().NotThrow();
    }

    [Test]
    public void Sample_WhenCpuThrowsPlatformNotSupported_MidFlight_DisablesOnlyCpu()
    {
        var callCount = 0;
        var logger = Substitute.For<ILogger<AppHealthMetricsCollector>>();
        using var collector = new AppHealthMetricsCollector(
            logger,
            getCpuTime: _ =>
            {
                callCount++;
                if (callCount > 2) throw new PlatformNotSupportedException();
                return TimeSpan.FromSeconds(callCount);
            });

        Thread.Sleep(1500);

        var snapshot = collector.GetSnapshot();
        snapshot.HasAny.Should().BeTrue();
        snapshot.CpuUsagePercent.Should().BeNull();
        snapshot.ManagedHeapMb.Should().NotBeNull();
        snapshot.WorkingSetMb.Should().NotBeNull();
        snapshot.ThreadCount.Should().NotBeNull();
        snapshot.ThreadPoolQueueLength.Should().NotBeNull();
        snapshot.GcGen2Collections.Should().NotBeNull();
        snapshot.UptimeSeconds.Should().NotBeNull();
    }

    [Test]
    public void GetSnapshot_AfterConstruction_HasSaneCpuValue()
    {
        using var collector = new AppHealthMetricsCollector(
            Substitute.For<ILogger<AppHealthMetricsCollector>>());

        Thread.Sleep(200);

        var snapshot = collector.GetSnapshot();
        snapshot.CpuUsagePercent.Should().NotBeNull();
        snapshot.CpuUsagePercent!.Value.Should().BeInRange(0.0, 100.0);
    }

    [Test]
    public void Constructor_WhenProbeThrowsNonPlatformNotSupported_DisablesOnlyThatMetric()
    {
        using var collector = new AppHealthMetricsCollector(
            Substitute.For<ILogger<AppHealthMetricsCollector>>(),
            getCpuTime: static _ => throw new InvalidOperationException("test"),
            getWorkingSet64: null,
            getThreadCount: null);

        var snapshot = collector.GetSnapshot();
        snapshot.HasAny.Should().BeTrue();
        snapshot.CpuUsagePercent.Should().BeNull();
        snapshot.WorkingSetMb.Should().NotBeNull();
        snapshot.ThreadCount.Should().NotBeNull();
        snapshot.ManagedHeapMb.Should().NotBeNull();
    }

    [Test]
    public void Sample_WhenProcessAcquisitionFails_DisablesProcessMetrics()
    {
        var logger = Substitute.For<ILogger<AppHealthMetricsCollector>>();
        using var collector = new AppHealthMetricsCollector(
            logger,
            getCpuTime: null,
            getWorkingSet64: null,
            getThreadCount: null,
            getCurrentProcess: static () => throw new PlatformNotSupportedException());

        Thread.Sleep(200);

        var snapshot = collector.GetSnapshot();
        snapshot.HasAny.Should().BeTrue();
        snapshot.CpuUsagePercent.Should().BeNull();
        snapshot.WorkingSetMb.Should().BeNull();
        snapshot.ThreadCount.Should().BeNull();
        snapshot.ManagedHeapMb.Should().NotBeNull();
        snapshot.ThreadPoolQueueLength.Should().NotBeNull();
        snapshot.GcGen2Collections.Should().NotBeNull();
        snapshot.UptimeSeconds.Should().NotBeNull();
    }

    [Test]
    public void Sample_WhenAllProcessMetricsUnavailable_SkipsProcessAcquisition()
    {
        var processAcquired = 0;
        using var collector = new AppHealthMetricsCollector(
            Substitute.For<ILogger<AppHealthMetricsCollector>>(),
            getCpuTime: static _ => throw new PlatformNotSupportedException(),
            getWorkingSet64: static _ => throw new PlatformNotSupportedException(),
            getThreadCount: static _ => throw new PlatformNotSupportedException(),
            getCurrentProcess: () => { processAcquired++; return Process.GetCurrentProcess(); });

        processAcquired = 0;

        Thread.Sleep(1500);

        processAcquired.Should().Be(0);
    }

    [Test]
    public void Dispose_PreventsFurtherSampling()
    {
        var sampleCount = 0;
        var getCpuTime = (Process _) =>
        {
            sampleCount++;
            return TimeSpan.FromSeconds(1);
        };
        var collector = new AppHealthMetricsCollector(
            Substitute.For<ILogger<AppHealthMetricsCollector>>(),
            getCpuTime: getCpuTime);

        Thread.Sleep(500);
        var beforeDispose = sampleCount;
        collector.Dispose();
        Thread.Sleep(1500);
        var afterDispose = sampleCount;

        afterDispose.Should().Be(beforeDispose);
    }
}
