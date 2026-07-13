using System.Threading;
using Microsoft.Extensions.Logging;
using MqttProbe.Services.Metrics;

namespace MqttProbe.Shared.Tests.Services.Metrics;

[TestFixture]
public class AppHealthMetricsCollectorTests
{
    [Test]
    public void GetSnapshot_WhenAvailable_ReturnsPopulatedSnapshot()
    {
        using var collector = new AppHealthMetricsCollector(
            Substitute.For<ILogger<AppHealthMetricsCollector>>());

        var snapshot = collector.GetSnapshot();

        snapshot.Available.Should().BeTrue();
        snapshot.CpuUsagePercent.Should().BeGreaterThanOrEqualTo(0);
        snapshot.ManagedHeapMb.Should().BeGreaterThanOrEqualTo(0);
        snapshot.WorkingSetMb.Should().BeGreaterThanOrEqualTo(0);
        snapshot.ThreadCount.Should().BeGreaterThan(0);
        snapshot.ThreadPoolQueueLength.Should().BeGreaterThanOrEqualTo(0);
        snapshot.GcGen2Collections.Should().BeGreaterThanOrEqualTo(0);
        snapshot.UptimeSeconds.Should().BeGreaterThanOrEqualTo(0);
    }

    [Test]
    public void GetSnapshot_WhenUnavailable_ReturnsUnavailableSnapshot()
    {
        using var collector = new AppHealthMetricsCollector(
            Substitute.For<ILogger<AppHealthMetricsCollector>>(),
            getCpuTime: static () => throw new PlatformNotSupportedException());

        var snapshot = collector.GetSnapshot();

        snapshot.Available.Should().BeFalse();
        snapshot.CpuUsagePercent.Should().Be(0);
        snapshot.ManagedHeapMb.Should().Be(0);
        snapshot.WorkingSetMb.Should().Be(0);
        snapshot.ThreadCount.Should().Be(0);
        snapshot.ThreadPoolQueueLength.Should().Be(0);
        snapshot.GcGen2Collections.Should().Be(0);
        snapshot.UptimeSeconds.Should().Be(0);
    }

    [Test]
    public void Constructor_WhenProcessAccessThrows_DoesNotThrow()
    {
        var act = () => new AppHealthMetricsCollector(
            Substitute.For<ILogger<AppHealthMetricsCollector>>(),
            getCpuTime: static () => throw new PlatformNotSupportedException());

        act.Should().NotThrow();
    }

    [Test]
    public void Constructor_WhenUnavailable_DoesNotStartTimer()
    {
        using var collector = new AppHealthMetricsCollector(
            Substitute.For<ILogger<AppHealthMetricsCollector>>(),
            getCpuTime: static () => throw new PlatformNotSupportedException());

        Thread.Sleep(150);

        var snapshot = collector.GetSnapshot();
        snapshot.Available.Should().BeFalse();
        snapshot.UptimeSeconds.Should().Be(0);
    }

    [Test]
    public void Constructor_WhenUnavailable_LogsDebugMessage()
    {
        var logger = Substitute.For<ILogger<AppHealthMetricsCollector>>();
        using var collector = new AppHealthMetricsCollector(
            logger,
            getCpuTime: static () => throw new PlatformNotSupportedException());

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

        // GetSnapshot should still return last-known values without exception
        var act = () => collector.GetSnapshot();
        act.Should().NotThrow();
    }

    [Test]
    public void Sample_WhenPlatformNotSupported_DisablesAvailabilityAndStopsSampling()
    {
        var callCount = 0;
        var logger = Substitute.For<ILogger<AppHealthMetricsCollector>>();
        using var collector = new AppHealthMetricsCollector(
            logger,
            getCpuTime: () =>
            {
                callCount++;
                if (callCount > 2) throw new PlatformNotSupportedException();
                return TimeSpan.FromSeconds(callCount);
            });

        Thread.Sleep(1500);

        var snapshot = collector.GetSnapshot();
        snapshot.Available.Should().BeFalse();
    }
}
