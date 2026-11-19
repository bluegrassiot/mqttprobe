using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Microsoft.JSInterop;
using MqttProbe.Core.Models.Chart;
using MqttProbe.Core.Services.Chart;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.UI.Services.Formatting;
using MqttProbe.UI.Tests.TestHelpers;
using ChartCardComponent = MqttProbe.UI.Components.Charts.ChartCard;

namespace MqttProbe.UI.Tests.Components.Charts;

[TestFixture]
public class ChartCardPendingApplyWorkTests : BunitTestContext
{
    private const int SeriesCount = 4;
    private const int PointsPerSeries = 5000;
    private const int Notifications = 20;

    private static readonly DateTimeOffset _start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private IChartDataService _chartData = null!;
    private IUiSettings _ui = null!;
    private ChartTimeFormatterStub _formatter = null!;
    private FakeTimeProvider _clock = null!;
    private SlowChartRuntime _runtime = null!;
    private SlowUpdateModule _module = null!;
    private readonly Dictionary<Guid, List<ChartDataPoint>> _points = [];

    [SetUp]
    public void SetupMocks()
    {
        _chartData = Substitute.For<IChartDataService>();
        _chartData.GetPoints(Arg.Any<Guid>())
            .Returns(call => (IReadOnlyList<ChartDataPoint>)PointsFor(call.Arg<Guid>()));
        _chartData.GetLastPointTimestamp(Arg.Any<Guid>())
            .Returns(call => LastTimestampFor(call.Arg<Guid>()));
        _chartData.GetPointBounds(Arg.Any<Guid>())
            .Returns(call => BoundsFor(call.Arg<Guid>()));
        _clock = new FakeTimeProvider(_start);
        _formatter = new ChartTimeFormatterStub();
        _ui = SettingsSubstituteRegistration.CreateUiSettings();

        Services.AddSingleton(_chartData);
        Services.AddSingleton(_ui);
        Services.AddSingleton<IDeviceTimeFormatter>(_formatter);
        _module = new SlowUpdateModule();
        _runtime = new SlowChartRuntime(JSInterop.JSRuntime) { Module = _module };
        Services.AddSingleton<IJSRuntime>(_runtime);
        EnsureMudProviders();
    }

    private IReadOnlyList<ChartDataPoint> PointsFor(Guid seriesId) =>
        _points.TryGetValue(seriesId, out var points) ? [.. points] : [];

    private DateTime? LastTimestampFor(Guid seriesId) =>
        _points.TryGetValue(seriesId, out var points) && points.Count > 0 ? points[^1].Timestamp : null;

    private (DateTime First, DateTime Last)? BoundsFor(Guid seriesId) =>
        _points.TryGetValue(seriesId, out var points) && points.Count > 0
            ? (points[0].Timestamp, points[^1].Timestamp)
            : null;

    private void Record(ChartSeries series)
    {
        if (!_points.TryGetValue(series.Id, out var points))
            _points[series.Id] = points = [];
        points.Add(new ChartDataPoint(_clock.GetUtcNow().UtcDateTime, points.Count));
    }

    private void RaiseData() => _chartData.OnDataUpdated += Raise.Event<Action>();

    private int PointReads() => _chartData.ReceivedCalls()
        .Count(call => call.GetMethodInfo().Name == nameof(IChartDataService.GetPoints));

    private int BoundReads() => _chartData.ReceivedCalls()
        .Count(call => call.GetMethodInfo().Name == nameof(IChartDataService.GetPointBounds));

    private IRenderedComponent<ChartCardComponent> RenderCard(ChartConfiguration configuration) =>
        Render<ChartCardComponent>(parameters => parameters
            .Add(c => c.Configuration, configuration)
            .Add(c => c.Clock, _clock)
            .Add(c => c.Renderer, ChartRendererKind.ECharts));

    private static void Rerender(IRenderedComponent<ChartCardComponent> cut, ChartConfiguration configuration) =>
        cut.Render(parameters => parameters
            .Add(c => c.Configuration, configuration)
            .Add(c => c.Renderer, ChartRendererKind.ECharts));

    private static Task PumpAsync(IRenderedComponent<ChartCardComponent> cut) =>
        cut.InvokeAsync(() => { });

    private async Task<IRenderedComponent<ChartCardComponent>> RenderCreatedAsync(ChartConfiguration chart)
    {
        var cut = RenderCard(chart);
        await _module.WaitForCreateAsync(1);
        return cut;
    }

    [Test]
    public async Task NotificationsDuringABlockedApply_KeepCaptionWorkButShareOneChartApply()
    {
        var series = Enumerable.Range(1, SeriesCount)
            .Select(index => new ChartSeries { DisplayName = $"Series {index}" })
            .ToArray();
        foreach (var item in series)
        {
            var points = _points[item.Id] = [];
            for (var index = 0; index < PointsPerSeries; index++)
                points.Add(new ChartDataPoint(_start.UtcDateTime.AddSeconds(-600 + (index * 0.1)), index));
        }

        var chart = new ChartConfiguration { Name = "Chart", Series = [.. series] };
        try
        {
            var cut = await RenderCreatedAsync(chart);
            _module.Creates[0].Series.Should().HaveCount(SeriesCount);
            _module.Creates[0].Series.Should().OnlyContain(item => item.Points.Count == PointsPerSeries);

            foreach (var item in series)
                Record(item);
            RaiseData();
            await _module.WaitForUpdateAsync(1);
            await PumpAsync(cut);

            var readsBefore = PointReads();
            var boundsBefore = BoundReads();
            var formatsBefore = _formatter.Requests.Count;

            // 500 ms steps cross the 5 s caption signature bucket on the fake clock, not on wall-clock waits.
            for (var index = 0; index < Notifications; index++)
            {
                _clock.Advance(TimeSpan.FromMilliseconds(500));
                foreach (var item in series)
                    Record(item);
                RaiseData();
                await PumpAsync(cut);
            }

            var reads = PointReads() - readsBefore;
            var boundsReads = BoundReads() - boundsBefore;
            var formats = _formatter.Requests.Count - formatsBefore;
            NUnit.Framework.TestContext.Out.WriteLine(
                $"pending apply: notifications={Notifications} series={SeriesCount} pointsPerSeries={PointsPerSeries} "
                + $"pointReads={reads} boundReads={boundsReads} captionFormats={formats} chartUpdates={_module.UpdateCount}");

            _module.UpdateCount.Should().Be(1, "notifications must not queue chart submissions behind the blocked apply");
            _module.MaxInFlight.Should().Be(1);
            reads.Should().Be(0,
                "a windowless caption reads the point bounds instead of taking a full snapshot per series");
            boundsReads.Should().BeGreaterThan(0, "the caption still tracks the data range");
            formats.Should().BeGreaterThan(0,
                    "the caption still formats while the chart apply is blocked")
                .And.BeLessThan(Notifications,
                    "caption requests are deduplicated by range signature, not by notification count");

            series[0].DisplayName = "Renamed while blocked";
            Rerender(cut, chart);
            await PumpAsync(cut);
            _module.UpdateCount.Should().Be(1,
                "the rename stays pending while the apply gate is held");

            _module.ReleaseAll();

            cut.WaitForAssertion(() => _module.UpdateCount.Should().Be(2), TimeSpan.FromSeconds(5));
            _module.Updates[1].Series.Should()
                .OnlyContain(item => item.Points.Count == PointsPerSeries + Notifications + 1);
            _module.Updates[1].Series[0].Name.Should().Be("Renamed while blocked");
            _module.MaxInFlight.Should().Be(1);
            cut.FindAll("button.chart-zone-token").Should().ContainSingle("the caption survives the pending apply");
        }
        finally
        {
            _module.ReleaseAll();
        }
    }
}
