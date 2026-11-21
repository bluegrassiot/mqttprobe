using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Microsoft.JSInterop;
using MqttProbe.Core.Models.Chart;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Services.Chart;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.UI.Services.Formatting;
using MqttProbe.UI.Tests.TestHelpers;
using ChartCardComponent = MqttProbe.UI.Components.Charts.ChartCard;

namespace MqttProbe.UI.Tests.Components.Charts;

[TestFixture]
public class ChartCardApplySingleFlightTests : BunitTestContext
{
    private static readonly DateTimeOffset _start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan _throttleClear = TimeSpan.FromMilliseconds(250);

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

    private void Record(ChartSeries series, double secondsAgo, double value = 1)
    {
        if (!_points.TryGetValue(series.Id, out var points))
            _points[series.Id] = points = [];
        points.Add(new ChartDataPoint(_clock.GetUtcNow().UtcDateTime.AddSeconds(-secondsAgo), value));
    }

    private void RaiseData() => _chartData.OnDataUpdated += Raise.Event<Action>();

    private static ChartConfiguration Chart(params ChartSeries[] series) => new()
    {
        Name = "Chart",
        Series = [.. series]
    };

    private IRenderedComponent<ChartCardComponent> RenderCard(ChartConfiguration configuration) =>
        Render<ChartCardComponent>(parameters => parameters
            .Add(c => c.Configuration, configuration)
            .Add(c => c.Clock, _clock));

    private static void Rerender(IRenderedComponent<ChartCardComponent> cut, ChartConfiguration configuration) =>
        cut.Render(parameters => parameters
            .Add(c => c.Configuration, configuration));

    private static long Epoch(double secondsAgo) =>
        new DateTimeOffset(_start.UtcDateTime.AddSeconds(-secondsAgo)).ToUnixTimeMilliseconds();

    private static Task FlushAsync(IRenderedComponent<ChartCardComponent> cut) =>
        cut.InvokeAsync(() => { });

    private async Task<IRenderedComponent<ChartCardComponent>> RenderCreatedAsync(ChartConfiguration chart)
    {
        var cut = RenderCard(chart);
        await _module.WaitForCreateAsync(1);
        return cut;
    }

    [Test]
    public async Task UpdatesQueuedWhileAnApplyIsBlocked_CoalesceIntoOneLatestSnapshot()
    {
        try
        {
            var series = new ChartSeries { DisplayName = "Temp" };
            Record(series, 30, 1);
            var cut = await RenderCreatedAsync(Chart(series));
            _module.Creates[0].Series[0].Points.Should().ContainSingle();

            Record(series, 20, 2);
            RaiseData();
            await _module.WaitForUpdateAsync(1);
            _module.UpdateCount.Should().Be(1, "only the first snapshot reaches interop while it is blocked");

            for (var index = 0; index < 3; index++)
            {
                await Task.Delay(_throttleClear);
                Record(series, 10 - index, 3 + index);
                RaiseData();
                await FlushAsync(cut);
            }

            _module.UpdateCount.Should().Be(1,
                "render cycles during a blocked apply must not build extra snapshots waiting on the gate");
            _module.MaxInFlight.Should().Be(1);

            _module.ReleaseAll();

            cut.WaitForAssertion(() => _module.UpdateCount.Should().Be(2), TimeSpan.FromSeconds(5));
            var followUp = _module.Updates[1];
            followUp.Series[0].Points.Should().HaveCount(5,
                "the follow-up draws the whole latest buffer, not a historical snapshot");
            followUp.Series[0].Points[^1].Should().Equal(Epoch(8), 5);
            _module.MaxInFlight.Should().Be(1);
            _module.Resets[1].Should().BeFalse();
        }
        finally
        {
            _module.ReleaseAll();
        }
    }

    [Test]
    public async Task DirtyUpdateDuringTheFollowUp_IsNotLost()
    {
        try
        {
            var series = new ChartSeries { DisplayName = "Temp" };
            Record(series, 30, 1);
            var chart = Chart(series);
            var cut = await RenderCreatedAsync(chart);

            Record(series, 20, 2);
            RaiseData();
            await _module.WaitForUpdateAsync(1);

            series.DisplayName = "Renamed";
            Rerender(cut, chart);
            await FlushAsync(cut);
            _module.UpdateCount.Should().Be(1, "the rename is pending while the first apply is blocked");

            _module.ReleaseNextUpdate();
            await _module.WaitForUpdateAsync(2);
            _module.Updates[1].Series[0].Name.Should().Be("Renamed");

            Record(series, 10, 3);
            series.DisplayName = "RenamedAgain";
            Rerender(cut, chart);
            await FlushAsync(cut);
            _module.UpdateCount.Should().Be(2, "the second rename is pending while the follow-up is in flight");

            _module.ReleaseAll();

            cut.WaitForAssertion(() => _module.UpdateCount.Should().Be(3), TimeSpan.FromSeconds(5));
            _module.Updates[2].Series[0].Name.Should().Be("RenamedAgain",
                "the follow-up loop must consume dirty state that arrived while it awaited interop");
            _module.Updates[2].Series[0].Points.Should().HaveCount(3,
                "the same follow-up rebuilds from the buffer mutated while the update was in flight");
        }
        finally
        {
            _module.ReleaseAll();
        }
    }

    [Test]
    public async Task PendingViewportResetAndOptions_AreAppliedByTheFollowUp()
    {
        try
        {
            var series = new ChartSeries { DisplayName = "Temp" };
            _chartData.GetPoints(series.Id).Returns(
            [
                new ChartDataPoint(DateTime.UtcNow.AddMinutes(-10), 1),
                new ChartDataPoint(DateTime.UtcNow.AddSeconds(-10), 2)
            ]);
            var chart = Chart(series);
            var cut = await RenderCreatedAsync(chart);
            _module.Creates[0].Series[0].Points.Should().HaveCount(2);

            RaiseData();
            await _module.WaitForUpdateAsync(1);

            _ui.Ui.MessageTime = new MessageTimePreferences
            {
                Zone = MessageTimePreferences.ZoneUtc,
                ClockFormat = MessageTimePreferences.ClockFormat12Hour
            };
            _ui.UiPreferencesChanged += Raise.Event<Action>();
            await FlushAsync(cut);

            chart.TimeWindowMinutes = 1;
            Rerender(cut, chart);
            await FlushAsync(cut);

            _module.ReleaseAll();

            cut.WaitForAssertion(() => _module.UpdateCount.Should().Be(2), TimeSpan.FromSeconds(5));
            var followUp = _module.Updates[1];
            followUp.Zone.Should().Be(MessageTimePreferences.ZoneUtc);
            followUp.ClockFormat.Should().Be(MessageTimePreferences.ClockFormat12Hour);
            _module.Resets[1].Should().BeTrue(
                "a viewport reset requested during the blocked apply survives to the follow-up");
            followUp.Series[0].Points.Should().ContainSingle().Which[1].Should().Be(2,
                "the narrower time window applies to the newest buffer");
        }
        finally
        {
            _module.ReleaseAll();
        }
    }

    [Test]
    public async Task PausingWhileAnApplyIsBlocked_KeepsDataUndrawnUntilResume()
    {
        try
        {
            var series = new ChartSeries { DisplayName = "Temp" };
            Record(series, 30, 1);
            var cut = await RenderCreatedAsync(Chart(series));

            Record(series, 20, 2);
            RaiseData();
            await _module.WaitForUpdateAsync(1);

            cut.Find("button[title='Pause live updates (keeps the view still)']").Click();
            await Task.Delay(_throttleClear);
            Record(series, 10, 3);
            RaiseData();
            await FlushAsync(cut);

            _module.ReleaseNextUpdate();
            await FlushAsync(cut);

            _module.UpdateCount.Should().Be(1,
                "pausing while the apply is in flight must stop the follow-up from drawing buffered data");

            cut.Find("button[title='Resume live updates']").Click();

            cut.WaitForAssertion(() => _module.UpdateCount.Should().Be(2), TimeSpan.FromSeconds(5));
            _module.Updates[1].Series[0].Points.Select(point => point[1]).Should().Equal([1, 2, 3],
                "resuming draws the latest buffer, including data that arrived while paused");
        }
        finally
        {
            _module.ReleaseAll();
        }
    }
}
