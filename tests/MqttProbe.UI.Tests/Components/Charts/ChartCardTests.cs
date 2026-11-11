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
        Services.AddUiSettings(SettingsSubstituteRegistration.CreateUiSettings());
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

    private static string StatusChipText(IRenderedComponent<ChartCardComponent> cut) =>
        cut.Find(".chart-card-status .mud-chip").TextContent.Trim();

    private static string? StatusAgeText(IRenderedComponent<ChartCardComponent> cut) =>
        cut.FindAll(".chart-card-status__age").SingleOrDefault()?.TextContent.Trim();

    private static string HeaderText(IRenderedComponent<ChartCardComponent> cut) =>
        cut.Find(".mud-card-header-content > .mud-typography-caption").TextContent;

    [Test]
    public void SeriesWithoutPoints_IsWaitingForData_NotLiveOrStale()
    {
        var chart = Chart(10, new ChartSeries { DisplayName = "Temp" });

        var cut = RenderCard(chart);

        StatusChipText(cut).Should().Be("Waiting for data");
        StatusAgeText(cut).Should().BeNull("waiting has no receipt to age");
        cut.FindAll(".chart-series-status__row").Should().BeEmpty();
        cut.FindAll(".chart-card-status__counts").Should().BeEmpty();
    }

    [Test]
    public void SharedIndicator_ShowsLiveAndTheNewestReceiptAge()
    {
        var a = new ChartSeries { DisplayName = "Temp" };
        var b = new ChartSeries { DisplayName = "Flow" };
        Record(a, 3);
        Record(b, 1);

        var cut = RenderCard(Chart(10, a, b));

        cut.FindAll(".chart-card-status .mud-chip").Should().ContainSingle();
        StatusChipText(cut).Should().Be("Live");
        StatusAgeText(cut).Should().Be("1s ago", "the newest point from any configured series drives the shared indicator");
    }

    [Test]
    public void OlderSeries_CannotMakeAFreshChartStaleOrMixed()
    {
        var a = new ChartSeries { DisplayName = "Temp" };
        var b = new ChartSeries { DisplayName = "Flow" };
        Record(a, 120);
        Record(b, 2);

        var cut = RenderCard(Chart(10, a, b));

        StatusChipText(cut).Should().Be("Live");
        StatusAgeText(cut).Should().Be("2s ago");
        cut.Markup.Should().NotContain("Stale").And.NotContain("Mixed");
    }

    [Test]
    public void WaitingSeries_CannotMakeAFreshChartMixed()
    {
        var a = new ChartSeries { DisplayName = "Temp" };
        var b = new ChartSeries { DisplayName = "Flow" };
        Record(b, 2);

        var cut = RenderCard(Chart(10, a, b));

        StatusChipText(cut).Should().Be("Live");
        cut.FindAll(".chart-card-status .mud-chip").Should().ContainSingle();
    }

    [Test]
    public void AllSeriesStale_ShowsStaleWithTheNewestReceiptAge()
    {
        var a = new ChartSeries { DisplayName = "Temp" };
        var b = new ChartSeries { DisplayName = "Flow" };
        Record(a, 40);
        Record(b, 90);

        var cut = RenderCard(Chart(10, a, b));

        StatusChipText(cut).Should().Be("Stale");
        StatusAgeText(cut).Should().Be("40s ago");
    }

    [Test]
    public void ExactlyAtTimeout_IsStillLive()
    {
        var a = new ChartSeries { DisplayName = "Temp" };
        Record(a, 10);

        var cut = RenderCard(Chart(10, a));

        StatusChipText(cut).Should().Be("Live");
        StatusAgeText(cut).Should().Be("10s ago");
    }

    [Test]
    public void FutureReceipt_StaysLiveWithoutShowingANegativeAge()
    {
        var a = new ChartSeries { DisplayName = "Temp" };
        Record(a, -5);

        var cut = RenderCard(Chart(10, a));

        StatusChipText(cut).Should().Be("Live");
        StatusAgeText(cut).Should().BeNull("an age in the future has no readable form");
    }

    [Test]
    public void DetectionOff_ShowsFreshnessOffWithoutCallingDataStale()
    {
        var a = new ChartSeries { DisplayName = "Temp" };
        Record(a, 300);

        var cut = RenderCard(Chart(null, a));

        StatusChipText(cut).Should().Be("Freshness off");
        StatusAgeText(cut).Should().BeNull();
        HeaderText(cut).Should().Contain("Freshness off").And.NotContain("stale after");
        cut.Markup.Should().NotContain("Stale");
    }

    [Test]
    public void DetectionOff_WithoutData_ShowsFreshnessOff()
    {
        var a = new ChartSeries { DisplayName = "Temp" };

        var cut = RenderCard(Chart(null, a));

        HeaderText(cut).Should().Contain("Freshness off").And.NotContain("stale after");
        StatusChipText(cut).Should().Be("Freshness off");
    }

    [Test]
    public void TurningDetectionOff_OnARenderedCard_UpdatesTheHeaderWithoutReload()
    {
        var a = new ChartSeries { DisplayName = "Temp" };
        Record(a, 300);
        var cut = RenderCard(Chart(10, a));
        HeaderText(cut).Should().Contain("stale after 10s");
        StatusChipText(cut).Should().Be("Stale");

        cut.Render(parameters => parameters
            .Add(c => c.Configuration, Chart(null, a))
            .Add(c => c.Clock, _clock));

        HeaderText(cut).Should().Contain("Freshness off").And.NotContain("stale after 10s");
        StatusChipText(cut).Should().Be("Freshness off");
        StatusAgeText(cut).Should().BeNull();
    }

    [Test]
    public void PauseTakesPrecedence_AndResumeReevaluatesFromReceiptTimes()
    {
        var a = new ChartSeries { DisplayName = "Temp" };
        Record(a, 2);
        var cut = RenderCard(Chart(10, a));
        StatusChipText(cut).Should().Be("Live");

        cut.Find("button[title='Pause live updates (enables zoom/pan/export)']").Click();
        cut.WaitForAssertion(() => StatusChipText(cut).Should().Be("Paused"), TimeSpan.FromSeconds(5));
        StatusAgeText(cut).Should().BeNull();

        _clock.Advance(TimeSpan.FromSeconds(60));
        StatusChipText(cut).Should().Be("Paused");

        cut.Find("button[title='Resume live updates']").Click();
        cut.WaitForAssertion(
            () => StatusChipText(cut).Should().Be("Stale", "resuming evaluates the real receipt time without waiting for a message"),
            TimeSpan.FromSeconds(5));
        StatusAgeText(cut).Should().Be("1m 2s ago", "pausing must not reset receipt times");
    }

    [Test]
    public void HoverPauseTakesPrecedence_AndLeavingRestoresFreshness()
    {
        var a = new ChartSeries { DisplayName = "Temp" };
        Record(a, 60);
        var cut = RenderCard(Chart(10, a));
        StatusChipText(cut).Should().Be("Stale");

        cut.Find(".chart-card-plot").MouseOver();
        cut.WaitForAssertion(() => StatusChipText(cut).Should().Be("Paused"), TimeSpan.FromSeconds(5));

        cut.Find(".chart-card-plot").TriggerEvent("onmouseleave", new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        cut.WaitForAssertion(() => StatusChipText(cut).Should().Be("Stale"), TimeSpan.FromSeconds(5));
    }

    [Test]
    public void EitherSeries_ReturnsTheSharedIndicatorToLiveWithoutReload()
    {
        var a = new ChartSeries { DisplayName = "Temp" };
        var b = new ChartSeries { DisplayName = "Flow" };
        Record(a, 60);
        Record(b, 90);
        var cut = RenderCard(Chart(10, a, b));
        StatusChipText(cut).Should().Be("Stale");

        Record(b, 0, 21.5);
        _chartData.OnDataUpdated += Raise.Event<Action>();

        cut.WaitForAssertion(() =>
        {
            StatusChipText(cut).Should().Be("Live");
            StatusAgeText(cut).Should().Be("just now");
        }, TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task Tick_AdvancesAgeWithoutNewMessages()
    {
        var a = new ChartSeries { DisplayName = "Temp" };
        Record(a, 2);
        var cut = RenderCard(Chart(10, a));
        StatusAgeText(cut).Should().Be("2s ago");

        _clock.Advance(TimeSpan.FromSeconds(5));
        await cut.InvokeAsync(() => cut.Instance.Tick());

        StatusAgeText(cut).Should().Be("7s ago");
        StatusChipText(cut).Should().Be("Live");
    }

    [Test]
    public async Task Tick_MarksTheChartStaleWithoutNewMessages()
    {
        var a = new ChartSeries { DisplayName = "Temp" };
        Record(a, 2);
        var cut = RenderCard(Chart(10, a));
        StatusChipText(cut).Should().Be("Live");

        _clock.Advance(TimeSpan.FromSeconds(9));
        await cut.InvokeAsync(() => cut.Instance.Tick());

        StatusChipText(cut).Should().Be("Stale");
    }

    [Test]
    public void UnrelatedSeriesUpdate_DoesNotResetTheSharedTimestamp()
    {
        var a = new ChartSeries { DisplayName = "Temp" };
        Record(a, 500);
        var cut = RenderCard(Chart(10, a));
        StatusChipText(cut).Should().Be("Stale");
        StatusAgeText(cut).Should().Be("8m 20s ago");

        Record(new ChartSeries { DisplayName = "Other chart" }, 0, 7);
        _chartData.OnDataUpdated += Raise.Event<Action>();

        StatusChipText(cut).Should().Be("Stale", "only the configured series drive this chart's freshness");
        StatusAgeText(cut).Should().Be("8m 20s ago");
    }

    [Test]
    public void DisplayWindow_DoesNotHideReceiptAge()
    {
        var a = new ChartSeries { DisplayName = "Temp" };
        Record(a, 600);
        var chart = Chart(10, a);
        chart.TimeWindowMinutes = 5;

        var cut = RenderCard(chart);

        StatusChipText(cut).Should().Be("Stale");
        StatusAgeText(cut).Should().Be("10m 0s ago", "the receipt age ignores the display window");
        cut.FindComponents<ApexCharts.ApexPointSeries<ChartDataPoint>>().Should().ContainSingle()
            .Which.Instance.Items.Should().BeEmpty("the point is outside the five-minute display window");
    }

    [Test]
    public void ChartWithoutSeries_KeepsTheEmptySeriesMessage()
    {
        var cut = RenderCard(Chart(10));

        cut.Markup.Should().Contain("No series added.");
        cut.FindAll(".chart-series-status__row").Should().BeEmpty();
        cut.FindAll(".chart-card-status__counts").Should().BeEmpty();
    }
}
