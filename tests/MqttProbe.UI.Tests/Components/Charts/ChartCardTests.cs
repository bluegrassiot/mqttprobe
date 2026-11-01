using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MqttProbe.Core.Models.Chart;
using MqttProbe.Core.Services.Chart;
using MqttProbe.UI.Tests.TestHelpers;
using ChartCardComponent = MqttProbe.UI.Components.Charts.ChartCard;

namespace MqttProbe.UI.Tests.Components.Charts;

[TestFixture]
public class ChartCardTests : BunitTestContext
{
    private static readonly DateTimeOffset _start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private IChartDataService _chartData = null!;
    private FakeTimeProvider _clock = null!;
    private readonly Dictionary<Guid, List<ChartDataPoint>> _points = [];

    [SetUp]
    public void SetupMocks()
    {
        _chartData = Substitute.For<IChartDataService>();
        _chartData.GetPoints(Arg.Any<Guid>())
            .Returns(call => (IReadOnlyList<ChartDataPoint>)PointsFor(call.Arg<Guid>()));
        _clock = new FakeTimeProvider(_start);

        Services.AddSingleton(_chartData);
        EnsureMudProviders();
    }

    private IReadOnlyList<ChartDataPoint> PointsFor(Guid seriesId) =>
        _points.TryGetValue(seriesId, out var points) ? [.. points] : [];

    private void Record(ChartSeries series, double secondsAgo, double value = 1)
    {
        if (!_points.TryGetValue(series.Id, out var points))
            _points[series.Id] = points = [];
        points.Add(new ChartDataPoint(_clock.GetUtcNow().UtcDateTime.AddSeconds(-secondsAgo), value));
    }

    private IRenderedComponent<ChartCardComponent> RenderCard(ChartConfiguration configuration) =>
        Render<ChartCardComponent>(parameters => parameters
            .Add(c => c.Configuration, configuration)
            .Add(c => c.Clock, _clock));

    private static ChartConfiguration Chart(double? staleTimeoutSeconds, params ChartSeries[] series) => new()
    {
        Name = "Chart",
        StaleTimeoutSeconds = staleTimeoutSeconds,
        Series = [.. series]
    };

    private static string SummaryText(IRenderedComponent<ChartCardComponent> cut) =>
        cut.Find(".chart-card-status .mud-chip").TextContent.Trim();

    private static string HeaderText(IRenderedComponent<ChartCardComponent> cut) =>
        cut.Find(".mud-card-header-content > .mud-typography-caption").TextContent;

    private static string RowText(IRenderedComponent<ChartCardComponent> cut, string seriesName) =>
        cut.FindAll(".chart-series-status__row")
            .Single(row => row.QuerySelector(".chart-series-status__name")!.TextContent.Trim() == seriesName)
            .TextContent;

    private static string CountsText(IRenderedComponent<ChartCardComponent> cut) =>
        cut.Find(".chart-card-status__counts").TextContent.Trim();

    [Test]
    public void SeriesWithoutPoints_IsWaitingForData_NotLiveOrStale()
    {
        var chart = Chart(10, new ChartSeries { DisplayName = "Temp" });

        var cut = RenderCard(chart);

        SummaryText(cut).Should().Be("Waiting for data");
        RowText(cut, "Temp").Should().Contain("Waiting for data");
        cut.FindAll(".chart-card-status__counts").Should().BeEmpty("the summary chip already covers every series");
    }

    [Test]
    public void AllSeriesFresh_ShowsLiveAndEachReceiptAge()
    {
        var a = new ChartSeries { DisplayName = "Temp" };
        var b = new ChartSeries { DisplayName = "Flow" };
        Record(a, 3);
        Record(b, 1);

        var cut = RenderCard(Chart(10, a, b));

        SummaryText(cut).Should().Be("Live");
        RowText(cut, "Temp").Should().Contain("Live").And.Contain("3s ago");
        RowText(cut, "Flow").Should().Contain("Live").And.Contain("1s ago");
        cut.FindAll(".chart-card-status__counts").Should().BeEmpty();
    }

    [Test]
    public void OneStaleSeries_IsNotHiddenByTheFreshSeries()
    {
        var a = new ChartSeries { DisplayName = "Temp" };
        var b = new ChartSeries { DisplayName = "Flow" };
        Record(a, 2);
        Record(b, 45);

        var cut = RenderCard(Chart(10, a, b));

        SummaryText(cut).Should().Be("Some series stale");
        RowText(cut, "Temp").Should().Contain("Live");
        RowText(cut, "Flow").Should().Contain("Stale").And.Contain("45s ago");
        CountsText(cut).Should().Be("1 live · 1 stale");
    }

    [Test]
    public void AllSeriesStale_ShowsStaleSummary()
    {
        var a = new ChartSeries { DisplayName = "Temp" };
        var b = new ChartSeries { DisplayName = "Flow" };
        Record(a, 40);
        Record(b, 90);

        var cut = RenderCard(Chart(10, a, b));

        SummaryText(cut).Should().Be("Stale");
        cut.FindAll(".chart-card-status__counts").Should().BeEmpty();
    }

    [Test]
    public void WaitingAndStaleSeries_StayDistinguishableInTheSummary()
    {
        var a = new ChartSeries { DisplayName = "Temp" };
        var b = new ChartSeries { DisplayName = "Flow" };
        Record(a, 40);

        var cut = RenderCard(Chart(10, a, b));

        SummaryText(cut).Should().Be("Mixed");
        RowText(cut, "Temp").Should().Contain("Stale");
        RowText(cut, "Flow").Should().Contain("Waiting for data");
        CountsText(cut).Should().Be("1 stale · 1 waiting");
    }

    [Test]
    public void ExactlyAtTimeout_IsStillLive()
    {
        var a = new ChartSeries { DisplayName = "Temp" };
        Record(a, 10);

        var cut = RenderCard(Chart(10, a));

        SummaryText(cut).Should().Be("Live");
        RowText(cut, "Temp").Should().Contain("10s ago");
    }

    [Test]
    public void DetectionOff_KeepsAgeAvailable_WithoutCallingDataStale()
    {
        var a = new ChartSeries { DisplayName = "Temp" };
        Record(a, 300);

        var cut = RenderCard(Chart(null, a));

        SummaryText(cut).Should().Be("Freshness off");
        HeaderText(cut).Should().Contain("Freshness off").And.NotContain("stale after");
        RowText(cut, "Temp").Should().Contain("Not checked").And.Contain("5m 0s ago");
        cut.Markup.Should().NotContain("Stale");
    }

    [Test]
    public void DetectionOff_WithoutData_ShowsFreshnessOffInTheHeader_AndKeepsWaitingChip()
    {
        var a = new ChartSeries { DisplayName = "Temp" };

        var cut = RenderCard(Chart(null, a));

        HeaderText(cut).Should().Contain("Freshness off").And.NotContain("stale after");
        SummaryText(cut).Should().Be("Waiting for data");
    }

    [Test]
    public void TurningDetectionOff_OnARenderedCard_UpdatesTheHeaderWithoutReload()
    {
        var a = new ChartSeries { DisplayName = "Temp" };
        Record(a, 300);
        var cut = RenderCard(Chart(10, a));
        HeaderText(cut).Should().Contain("stale after 10s");
        SummaryText(cut).Should().Be("Stale");

        cut.Render(parameters => parameters
            .Add(c => c.Configuration, Chart(null, a))
            .Add(c => c.Clock, _clock));

        HeaderText(cut).Should().Contain("Freshness off").And.NotContain("stale after 10s");
        SummaryText(cut).Should().Be("Freshness off");
        RowText(cut, "Temp").Should().Contain("Not checked");
    }

    [Test]
    public void TurningDetectionOff_WithNoData_FlipsTheHeaderButStaysWaiting()
    {
        var a = new ChartSeries { DisplayName = "Temp" };
        var cut = RenderCard(Chart(10, a));
        HeaderText(cut).Should().Contain("stale after 10s");

        cut.Render(parameters => parameters
            .Add(c => c.Configuration, Chart(null, a))
            .Add(c => c.Clock, _clock));

        HeaderText(cut).Should().Contain("Freshness off");
        SummaryText(cut).Should().Be("Waiting for data", "no data is still waiting, not fresh");
    }

    [Test]
    public void PauseTakesPrecedence_AndResumeReevaluatesFromReceiptTimes()
    {
        var a = new ChartSeries { DisplayName = "Temp" };
        Record(a, 2);
        var cut = RenderCard(Chart(10, a));
        SummaryText(cut).Should().Be("Live");

        cut.Find("button[title='Pause live updates (enables zoom/pan/export)']").Click();
        cut.WaitForAssertion(() => SummaryText(cut).Should().Be("Paused"), TimeSpan.FromSeconds(5));

        _clock.Advance(TimeSpan.FromSeconds(60));
        SummaryText(cut).Should().Be("Paused");

        cut.Find("button[title='Resume live updates']").Click();
        cut.WaitForAssertion(
            () => SummaryText(cut).Should().Be("Stale", "resuming evaluates the real receipt time without waiting for a message"),
            TimeSpan.FromSeconds(5));
        RowText(cut, "Temp").Should().Contain("1m 2s ago", "pausing must not reset receipt times");
    }

    [Test]
    public void HoverPauseTakesPrecedence_AndLeavingRestoresFreshness()
    {
        var a = new ChartSeries { DisplayName = "Temp" };
        Record(a, 60);
        var cut = RenderCard(Chart(10, a));
        SummaryText(cut).Should().Be("Stale");

        cut.Find(".chart-card-plot").MouseOver();
        cut.WaitForAssertion(() => SummaryText(cut).Should().Be("Paused"), TimeSpan.FromSeconds(5));

        cut.Find(".chart-card-plot").TriggerEvent("onmouseleave", new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        cut.WaitForAssertion(() => SummaryText(cut).Should().Be("Stale"), TimeSpan.FromSeconds(5));
    }

    [Test]
    public void NewPoint_ReturnsSeriesToLiveWithoutReload()
    {
        var a = new ChartSeries { DisplayName = "Temp" };
        Record(a, 60);
        var cut = RenderCard(Chart(10, a));
        SummaryText(cut).Should().Be("Stale");

        Record(a, 0, 21.5);
        _chartData.OnDataUpdated += Raise.Event<Action>();

        cut.WaitForAssertion(() =>
        {
            SummaryText(cut).Should().Be("Live");
            RowText(cut, "Temp").Should().Contain("just now");
        }, TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task Tick_AdvancesAgeWithoutNewMessages()
    {
        var a = new ChartSeries { DisplayName = "Temp" };
        Record(a, 2);
        var cut = RenderCard(Chart(10, a));
        RowText(cut, "Temp").Should().Contain("2s ago");

        _clock.Advance(TimeSpan.FromSeconds(5));
        await cut.InvokeAsync(() => cut.Instance.Tick());

        RowText(cut, "Temp").Should().Contain("7s ago");
        SummaryText(cut).Should().Be("Live");
    }

    [Test]
    public async Task Tick_MarksSeriesStaleWithoutNewMessages()
    {
        var a = new ChartSeries { DisplayName = "Temp" };
        Record(a, 2);
        var cut = RenderCard(Chart(10, a));
        SummaryText(cut).Should().Be("Live");

        _clock.Advance(TimeSpan.FromSeconds(9));
        await cut.InvokeAsync(() => cut.Instance.Tick());

        SummaryText(cut).Should().Be("Stale");
    }

    [Test]
    public void DisplayWindow_DoesNotHideReceiptAge()
    {
        var a = new ChartSeries { DisplayName = "Temp" };
        Record(a, 600);
        var chart = Chart(10, a);
        chart.TimeWindowMinutes = 5;

        var cut = RenderCard(chart);

        SummaryText(cut).Should().Be("Stale");
        RowText(cut, "Temp").Should().Contain("10m 0s ago", "the receipt age ignores the display window");
        cut.FindComponents<ApexCharts.ApexPointSeries<ChartDataPoint>>().Should().ContainSingle()
            .Which.Instance.Items.Should().BeEmpty("the point is outside the five-minute display window");
    }

    [Test]
    public void LongTopicAndPath_RenderInFullForIdentification()
    {
        const string topic = "spBv1.0/plant-floor-west/DDATA/packaging-line-04/carton-sealer-17";
        var a = new ChartSeries
        {
            DisplayName = "Seal temperature setpoint",
            Topic = topic,
            JsonPath = "metrics.Scalar/Seal Temperature Setpoint"
        };

        var cut = RenderCard(Chart(10, a));

        var row = cut.Find(".chart-series-status__row");
        row.QuerySelector(".chart-series-status__source")!.TextContent.Should().Contain(topic);
        row.QuerySelector(".chart-series-status__name")!.TextContent.Should().Be("Seal temperature setpoint");
    }

    [Test]
    public void ChartWithoutSeries_KeepsTheEmptySeriesMessage()
    {
        var cut = RenderCard(Chart(10));

        cut.Markup.Should().Contain("No series added.");
        cut.FindAll(".chart-series-status__row").Should().BeEmpty();
    }
}
