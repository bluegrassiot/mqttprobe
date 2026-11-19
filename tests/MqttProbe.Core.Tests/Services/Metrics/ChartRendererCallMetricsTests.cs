using Microsoft.Extensions.Logging.Abstractions;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Metrics;

namespace MqttProbe.Core.Tests.Services.Metrics;

[TestFixture]
public class ChartRendererCallMetricsTests
{
    private IPerformanceSettings _settings = null!;
    private IAppHealthMetricsCollector _health = null!;
    private long _nowMs;
    private UxMetricsService _service = null!;

    [SetUp]
    public void Setup()
    {
        _settings = Substitute.For<IPerformanceSettings>();
        _settings.Performance.Returns(new AppConfiguration().Performance);
        _health = Substitute.For<IAppHealthMetricsCollector>();
        _health.GetSnapshot().Returns(new AppHealthMetricsSnapshot(
            CpuUsagePercent: 1.0, ManagedHeapMb: 2.0,
            WorkingSetMb: 3.0, ThreadCount: 4, ThreadPoolQueueLength: 5,
            GcGen2Collections: 6, UptimeSeconds: 7.0));
        _nowMs = 0;
        _service = new UxMetricsService(
            NullLogger<UxMetricsService>.Instance,
            _settings,
            _health,
            () => Volatile.Read(ref _nowMs));
    }

    [TearDown]
    public void TearDown() => _health.Dispose();

    private ChartRendererCallMetricsSnapshot Renderer(string renderer) =>
        _service.GetSnapshot().ChartRendererCalls!.Single(call => call.Renderer == renderer);

    [Test]
    public void GetSnapshot_WithoutRendererCalls_LeavesTheListNull()
    {
        _service.GetSnapshot().ChartRendererCalls.Should().BeNull();
    }

    [Test]
    public void RecordChartRendererCall_AccumulatesCountsPerRenderer()
    {
        _service.RecordChartRendererCall("Apex", TimeSpan.FromMilliseconds(3), failed: false);
        _service.RecordChartRendererCall("Apex", TimeSpan.FromMilliseconds(5), failed: false);
        _service.RecordChartRendererCall("ECharts", TimeSpan.FromMilliseconds(7), failed: true);

        var snapshot = _service.GetSnapshot();
        snapshot.ChartRendererCalls.Should().HaveCount(2);
        snapshot.ChartRendererCalls![0].Should().BeEquivalentTo(
            new ChartRendererCallMetricsSnapshot("Apex", 2, 0, 4, 5, 0));
        snapshot.ChartRendererCalls[1].Should().BeEquivalentTo(
            new ChartRendererCallMetricsSnapshot("ECharts", 1, 1, 7, 7, 0),
            "a failed call still settles, so it counts as completed as well as failed");
    }

    [Test]
    public void RecordChartRendererCall_IncludesFailuresInTheDurationWindow()
    {
        _service.RecordChartRendererCall("ECharts", TimeSpan.FromMilliseconds(10), failed: true);
        _service.RecordChartRendererCall("ECharts", TimeSpan.FromMilliseconds(30), failed: false);

        var renderer = Renderer("ECharts");
        renderer.CompletedCalls.Should().Be(2);
        renderer.FailedCalls.Should().Be(1, "failures are a subset of the settled calls");
        renderer.AverageCallMilliseconds.Should().Be(20);
        renderer.MaximumCallMilliseconds.Should().Be(30);
    }

    [Test]
    public void RecordChartRendererCall_KeepsCountsButAveragesOnlyTheLastWindow()
    {
        _service.RecordChartRendererCall("Apex", TimeSpan.FromMilliseconds(1000), failed: false);
        var window = UxMetricsService.ChartRendererCallWindow;
        for (var index = 1; index < window + 1; index++)
            _service.RecordChartRendererCall("Apex", TimeSpan.FromMilliseconds(2), failed: false);

        var renderer = Renderer("Apex");
        renderer.CompletedCalls.Should().Be(window + 1);
        renderer.AverageCallMilliseconds.Should().Be(2, "the 1000 ms sample falls out of the fixed window");
        renderer.MaximumCallMilliseconds.Should().Be(2);
    }

    [Test]
    public void RecordChartRendererCall_ReportsAgeFromTheInjectedTick()
    {
        _nowMs = 1_000;
        _service.RecordChartRendererCall("Apex", TimeSpan.FromMilliseconds(4), failed: false);

        _nowMs = 6_500;
        Renderer("Apex").LastCompletedCallAgeSeconds.Should().Be(5.5);
    }

    [Test]
    public void RecordChartRendererCall_IgnoresUnknownRendererNames()
    {
        _service.RecordChartRendererCall("apex", TimeSpan.FromMilliseconds(4), failed: false);
        _service.RecordChartRendererCall("", TimeSpan.FromMilliseconds(4), failed: true);

        _service.GetSnapshot().ChartRendererCalls.Should().BeNull();
    }

    [Test]
    public void RecordChartRendererCall_StaysConsistentUnderConcurrentWrites()
    {
        const int writers = 8;
        const int callsPerWriter = 250;

        Parallel.For(0, writers, _ =>
        {
            for (var index = 0; index < callsPerWriter; index++)
                _service.RecordChartRendererCall("ECharts", TimeSpan.FromMilliseconds(2), failed: false);
        });

        var renderer = Renderer("ECharts");
        renderer.CompletedCalls.Should().Be(writers * callsPerWriter);
        renderer.FailedCalls.Should().Be(0);
        renderer.AverageCallMilliseconds.Should().Be(2);
        renderer.MaximumCallMilliseconds.Should().Be(2);
    }
}
