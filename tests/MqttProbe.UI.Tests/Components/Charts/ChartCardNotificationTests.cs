using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MqttProbe.Core.Models.Chart;
using MqttProbe.Core.Services.Chart;
using MqttProbe.UI.Tests.TestHelpers;
using ChartCardComponent = MqttProbe.UI.Components.Charts.ChartCard;

namespace MqttProbe.UI.Tests.Components.Charts;

[TestFixture]
public class ChartCardNotificationTests : BunitTestContext
{
    private static readonly DateTimeOffset _start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private IChartDataService _chartData = null!;
    private FakeTimeProvider _clock = null!;
    private readonly Dictionary<Guid, List<ChartDataPoint>> _points = [];
    private int _receiptLookups;

    [SetUp]
    public void SetupMocks()
    {
        _chartData = Substitute.For<IChartDataService>();
        _chartData.GetPoints(Arg.Any<Guid>())
            .Returns(call => (IReadOnlyList<ChartDataPoint>)PointsFor(call.Arg<Guid>()));
        _chartData.GetLastPointTimestamp(Arg.Any<Guid>())
            .Returns(call =>
            {
                Interlocked.Increment(ref _receiptLookups);
                return LastTimestampFor(call.Arg<Guid>());
            });
        _clock = new FakeTimeProvider(_start);

        Services.AddSingleton(_chartData);
        Services.AddUiSettings(SettingsSubstituteRegistration.CreateUiSettings());
        EnsureMudProviders();
    }

    private IReadOnlyList<ChartDataPoint> PointsFor(Guid seriesId) =>
        _points.TryGetValue(seriesId, out var points) ? [.. points] : [];

    private DateTime? LastTimestampFor(Guid seriesId) =>
        _points.TryGetValue(seriesId, out var points) && points.Count > 0 ? points[^1].Timestamp : null;

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

    [Test]
    public async Task NotificationBurst_AppliesTheLatestReceiptWithoutLosingIt()
    {
        var series = new ChartSeries { DisplayName = "Temp" };
        Record(series, 60);
        var cut = RenderCard(Chart(10, series));
        StatusChipText(cut).Should().Be("Stale");

        Record(series, 0, 21.5);
        await Task.Run(() =>
        {
            for (var index = 0; index < 50; index++)
                _chartData.OnDataUpdated += Raise.Event<Action>();
        });

        cut.WaitForAssertion(
            () => StatusChipText(cut).Should().Be("Live", "a coalesced burst must still apply the newest receipt"),
            TimeSpan.FromSeconds(5));
    }

    [Test]
    public void Notification_ReadsTheNewestReceiptTimestampFromTheService()
    {
        var series = new ChartSeries { DisplayName = "Temp" };
        Record(series, 60);
        var cut = RenderCard(Chart(10, series));
        _chartData.ClearReceivedCalls();

        Record(series, 0, 21.5);
        _chartData.OnDataUpdated += Raise.Event<Action>();

        cut.WaitForAssertion(() => StatusChipText(cut).Should().Be("Live"), TimeSpan.FromSeconds(5));
        _chartData.Received(1).GetLastPointTimestamp(series.Id);
    }

    [Test]
    public void NotificationBurst_WhilePausedAppliesTheLatestReceiptOnResume()
    {
        var series = new ChartSeries { DisplayName = "Temp" };
        Record(series, 60);
        var cut = RenderCard(Chart(10, series));
        cut.Find("button[title='Pause live updates (enables zoom/pan/export)']").Click();
        cut.WaitForAssertion(() => StatusChipText(cut).Should().Be("Paused"), TimeSpan.FromSeconds(5));

        Record(series, 0, 21.5);
        for (var index = 0; index < 20; index++)
            _chartData.OnDataUpdated += Raise.Event<Action>();

        cut.Find("button[title='Resume live updates']").Click();
        cut.WaitForAssertion(() => StatusChipText(cut).Should().Be("Live"), TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task QueuedNotificationAfterDispose_TakesNoReceiptLookup()
    {
        var series = new ChartSeries { DisplayName = "Temp" };
        Record(series, 60);
        var cut = RenderCard(Chart(10, series));

        await Task.Run(() => _chartData.OnDataUpdated += Raise.Event<Action>());
        await cut.Instance.DisposeAsync();
        var afterDispose = Volatile.Read(ref _receiptLookups);

        await cut.InvokeAsync(() => { });

        Volatile.Read(ref _receiptLookups).Should().Be(afterDispose,
            "a card disposed before its queued pass runs must not read receipts");
    }
}
