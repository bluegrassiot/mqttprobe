using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MqttProbe.Core.Models.Chart;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Services.Chart;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.UI.Services.Formatting;
using MqttProbe.UI.Tests.TestHelpers;
using MudBlazor;
using MudBlazor.Extensions;
using ChartCardComponent = MqttProbe.UI.Components.Charts.ChartCard;

namespace MqttProbe.UI.Tests.Components.Charts;

[TestFixture]
public class ChartCardTimeTests : BunitTestContext
{
    private static readonly DateTimeOffset _start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private IChartDataService _chartData = null!;
    private IUiSettings _ui = null!;
    private ChartTimeFormatterStub _formatter = null!;
    private FakeTimeProvider _clock = null!;
    private ChartConfiguration _configuration = null!;

    [SetUp]
    public void SetupMocks()
    {
        _chartData = Substitute.For<IChartDataService>();
        _chartData.GetPoints(Arg.Any<Guid>()).Returns([]);
        _clock = new FakeTimeProvider(_start);
        _ui = SettingsSubstituteRegistration.CreateUiSettings();
        _formatter = new ChartTimeFormatterStub();

        Services.AddSingleton(_chartData);
        Services.AddSingleton(_ui);
        Services.AddSingleton<IDeviceTimeFormatter>(_formatter);
        EnsureMudProviders();

        var module = JSInterop.SetupModule(ChartRendererInterop.ModulePath);
        module.Setup<bool>("createChart", _ => true).SetResult(true);
        module.Setup<bool>("updateChart", _ => true).SetResult(true);
        module.Setup<bool>("disposeChart", _ => true).SetResult(true);
    }

    private IRenderedComponent<ChartCardComponent> RenderCard(params ChartSeries[] series)
    {
        _configuration = new ChartConfiguration { Name = "Chart", Series = [.. series] };
        return Render<ChartCardComponent>(parameters => parameters
            .Add(c => c.Configuration, _configuration)
            .Add(c => c.Clock, _clock));
    }

    private static bool TooltipVisible(IRenderedComponent<ChartCardComponent> cut) =>
        cut.FindComponent<MudTooltip>().Instance.GetState(x => x.Visible);

    private static string SummaryText(IRenderedComponent<ChartCardComponent> cut) =>
        cut.Find(".chart-card-status .mud-chip").TextContent.Trim();

    private static long Epoch(DateTime timestamp) =>
        new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    private ChartRendererSpec CreateSpec(int index = 0) =>
        JSInterop.Invocations["createChart"][index].Arguments[3]
            .Should().BeOfType<ChartRendererSpec>().Subject;

    private ChartRendererSpec UpdateSpec(int index = 0) =>
        JSInterop.Invocations["updateChart"][index].Arguments[1]
            .Should().BeOfType<ChartRendererSpec>().Subject;

    [Test]
    public void RendererReceivesTheLocalTwelveHourPreferences()
    {
        _ui.Ui.MessageTime = new MessageTimePreferences
        {
            Zone = MessageTimePreferences.ZoneLocal,
            ClockFormat = MessageTimePreferences.ClockFormat12Hour
        };

        var cut = RenderCard(new ChartSeries { DisplayName = "Temp" });

        cut.WaitForAssertion(
            () => JSInterop.Invocations["createChart"].Should().ContainSingle(),
            TimeSpan.FromSeconds(5));
        var spec = CreateSpec();
        spec.Zone.Should().Be(MessageTimePreferences.ZoneLocal);
        spec.ClockFormat.Should().Be(MessageTimePreferences.ClockFormat12Hour);
    }

    [Test]
    public void RendererReceivesTheUtcTwentyFourHourPreferences()
    {
        _ui.Ui.MessageTime = new MessageTimePreferences
        {
            Zone = MessageTimePreferences.ZoneUtc,
            ClockFormat = MessageTimePreferences.ClockFormat24Hour
        };

        var cut = RenderCard(new ChartSeries { DisplayName = "Temp" });

        cut.WaitForAssertion(
            () => JSInterop.Invocations["createChart"].Should().ContainSingle(),
            TimeSpan.FromSeconds(5));
        var spec = CreateSpec();
        spec.Zone.Should().Be(MessageTimePreferences.ZoneUtc);
        spec.ClockFormat.Should().Be(MessageTimePreferences.ClockFormat24Hour);
    }

    [Test]
    public async Task ChartWaitsForTheBridge_ThenRendersAndNamesTheZoneOnce()
    {
        _ui.Ui.MessageTime = new MessageTimePreferences { Zone = MessageTimePreferences.ZoneLocal };
        _formatter.BridgeReady = false;
        _formatter.ZoneLabel = "America/New_York";
        _formatter.ZoneName = "Eastern Standard Time";
        _formatter.Abbreviation = "EST";
        _formatter.OffsetLabel = "UTC-05:00";

        var cut = RenderCard(new ChartSeries { DisplayName = "Temp" });

        cut.FindAll(".chart-card-echarts").Should()
            .BeEmpty("the plot waits for the shared time bridge first");
        cut.FindAll(".chart-card-plot__pending").Should().ContainSingle();

        _formatter.BridgeReady = true;
        await cut.InvokeAsync(() => cut.Instance.EnsureTimeBridgeAsync());

        cut.WaitForAssertion(
            () => cut.FindAll(".chart-card-echarts").Should().ContainSingle(),
            TimeSpan.FromSeconds(5));
        cut.WaitForAssertion(
            () => JSInterop.Invocations["createChart"].Should().ContainSingle(),
            TimeSpan.FromSeconds(5));

        var zone = cut.FindAll("button.chart-zone-token");
        zone.Should().ContainSingle("timezone indication is quiet and shown once per chart");
        zone[0].TextContent.Trim().Should().Be("EST");
        zone[0].GetAttribute("aria-label").Should()
            .Contain("America/New_York")
            .And.Contain("Eastern Standard Time")
            .And.Contain("UTC-05:00");
        _formatter.Requests.Should().ContainSingle().Which.Zone.Should().Be(MessageTimePreferences.ZoneLocal);
    }

    [Test]
    public void ChangingThePreference_ReformatsTheChartAndKeepsPointInstants()
    {
        _ui.Ui.MessageTime = new MessageTimePreferences
        {
            Zone = MessageTimePreferences.ZoneUtc,
            ClockFormat = MessageTimePreferences.ClockFormat24Hour
        };
        var series = new ChartSeries { DisplayName = "Temp" };
        var point = new ChartDataPoint(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc), 3);
        _chartData.GetPoints(series.Id).Returns([point]);

        var cut = RenderCard(series);
        cut.WaitForAssertion(
            () => JSInterop.Invocations["createChart"].Should().ContainSingle(),
            TimeSpan.FromSeconds(5));
        var beforeX = CreateSpec().Series[0].Points.Should().ContainSingle().Which[0];
        beforeX.Should().Be(new DateTimeOffset(point.Timestamp).ToUnixTimeMilliseconds());

        _formatter.ZoneLabel = "Europe/London";
        _formatter.ZoneName = "British Summer Time";
        _formatter.Abbreviation = "BST";
        _formatter.OffsetLabel = "UTC+01:00";
        _ui.Ui.MessageTime = new MessageTimePreferences
        {
            Zone = MessageTimePreferences.ZoneLocal,
            ClockFormat = MessageTimePreferences.ClockFormat12Hour
        };
        _ui.UiPreferencesChanged += Raise.Event<Action>();

        cut.Render(parameters => parameters
            .Add(c => c.Configuration, _configuration)
            .Add(c => c.Clock, _clock));

        cut.WaitForAssertion(() =>
        {
            JSInterop.Invocations["updateChart"].Should().ContainSingle();
            var update = UpdateSpec();
            update.Zone.Should().Be(MessageTimePreferences.ZoneLocal);
            update.ClockFormat.Should().Be(MessageTimePreferences.ClockFormat12Hour);
            cut.Find("button.chart-zone-token").TextContent.Trim().Should().Be("BST");
            cut.Find("button.chart-zone-token").GetAttribute("aria-label").Should().Contain("Europe/London");
        }, TimeSpan.FromSeconds(5));

        UpdateSpec().Series[0].Points.Should().ContainSingle().Which[0].Should()
            .Be(beforeX, "display preferences must not change the stored instant");
    }

    [Test]
    public void ZoneCaption_ShowsTheShortAbbreviationAndFullDetails()
    {
        _ui.Ui.MessageTime = new MessageTimePreferences { Zone = MessageTimePreferences.ZoneLocal };
        _formatter.ZoneLabel = "America/New_York";
        _formatter.ZoneName = "Eastern Standard Time";
        _formatter.Abbreviation = "EST";
        _formatter.OffsetLabel = "UTC-05:00";
        var series = new ChartSeries { DisplayName = "Temp" };
        _chartData.GetPoints(series.Id).Returns(
        [
            new ChartDataPoint(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc), 1)
        ]);

        var cut = RenderCard(series);

        cut.WaitForAssertion(() =>
        {
            var zone = cut.Find("button.chart-zone-token");
            zone.TextContent.Trim().Should().Be("EST");
            var details = zone.GetAttribute("aria-label");
            details.Should()
                .Contain("America/New_York")
                .And.Contain("Eastern Standard Time")
                .And.Contain("UTC-05:00");
            cut.FindComponent<MudTooltip>().Instance.Text.Should().Be(
                "America/New_York · Eastern Standard Time (UTC-05:00)");
        }, TimeSpan.FromSeconds(5));
    }

    [Test]
    public void ZoneCaption_ShowsBothAbbreviationsWhenTheVisibleRangeCrossesDst()
    {
        var transition = new DateTimeOffset(2026, 11, 1, 6, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        _ui.Ui.MessageTime = new MessageTimePreferences { Zone = MessageTimePreferences.ZoneLocal };
        _formatter.ZoneLabel = "America/New_York";
        _formatter.ItemFor = epoch => epoch < transition
            ? ("EDT", "Eastern Daylight Time", "UTC-04:00")
            : ("EST", "Eastern Standard Time", "UTC-05:00");
        var series = new ChartSeries { DisplayName = "Temp" };
        _chartData.GetPoints(series.Id).Returns(
        [
            new ChartDataPoint(new DateTime(2026, 11, 1, 5, 58, 0, DateTimeKind.Utc), 1),
            new ChartDataPoint(new DateTime(2026, 11, 1, 6, 2, 0, DateTimeKind.Utc), 2)
        ]);

        var cut = RenderCard(series);

        cut.WaitForAssertion(() =>
        {
            var zone = cut.Find("button.chart-zone-token");
            zone.TextContent.Trim().Should().Be("EDT / EST");
            zone.GetAttribute("aria-label").Should()
                .Contain("Eastern Daylight Time (UTC-04:00)")
                .And.Contain("Eastern Standard Time (UTC-05:00)");
        }, TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task ZoneCaption_FollowsTheVisibleZoomRangeAndResetsToTheDataRange()
    {
        var transition = new DateTimeOffset(2026, 11, 1, 6, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        _ui.Ui.MessageTime = new MessageTimePreferences { Zone = MessageTimePreferences.ZoneLocal };
        _formatter.ZoneLabel = "America/New_York";
        _formatter.ItemFor = epoch => epoch < transition
            ? ("EDT", "Eastern Daylight Time", "UTC-04:00")
            : ("EST", "Eastern Standard Time", "UTC-05:00");
        var series = new ChartSeries { DisplayName = "Temp" };
        _chartData.GetPoints(series.Id).Returns(
        [
            new ChartDataPoint(new DateTime(2026, 11, 1, 5, 58, 0, DateTimeKind.Utc), 1),
            new ChartDataPoint(new DateTime(2026, 11, 1, 6, 2, 0, DateTimeKind.Utc), 2)
        ]);

        var cut = RenderCard(series);
        cut.WaitForAssertion(
            () => cut.Find("button.chart-zone-token").TextContent.Trim().Should().Be("EDT / EST"),
            TimeSpan.FromSeconds(5));

        await cut.InvokeAsync(() => cut.Instance.OnEChartsViewportChanged(transition, transition + 60_000));

        cut.WaitForAssertion(
            () => cut.Find("button.chart-zone-token").TextContent.Trim().Should().Be("EST"),
            TimeSpan.FromSeconds(5));

        await cut.InvokeAsync(() => cut.Instance.OnEChartsViewportChanged(null, null));

        cut.WaitForAssertion(
            () => cut.Find("button.chart-zone-token").TextContent.Trim().Should().Be("EDT / EST"),
            TimeSpan.FromSeconds(5));
    }

    [Test]
    public void ZoneCaption_RevealsTheFullZoneOnHoverFocusAndTap()
    {
        _ui.Ui.MessageTime = new MessageTimePreferences { Zone = MessageTimePreferences.ZoneLocal };
        _formatter.ZoneLabel = "America/New_York";
        _formatter.ZoneName = "Eastern Standard Time";
        _formatter.Abbreviation = "EST";
        _formatter.OffsetLabel = "UTC-05:00";
        var series = new ChartSeries { DisplayName = "Temp" };
        _chartData.GetPoints(series.Id).Returns(
        [
            new ChartDataPoint(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc), 1)
        ]);

        var cut = RenderCard(series);
        cut.WaitForAssertion(
            () => cut.Find("button.chart-zone-token").TextContent.Trim().Should().Be("EST"),
            TimeSpan.FromSeconds(5));

        const string details = "America/New_York · Eastern Standard Time (UTC-05:00)";
        var button = cut.Find("button.chart-zone-token");
        button.GetAttribute("aria-label").Should().Be($"EST: {details}");
        cut.FindComponent<MudTooltip>().Instance.Text.Should().Be(details);
        TooltipVisible(cut).Should().BeFalse();

        button.TriggerEvent("onfocus", EventArgs.Empty);
        TooltipVisible(cut).Should().BeTrue("keyboard focus reveals the full zone");
        button.TriggerEvent("onblur", EventArgs.Empty);
        TooltipVisible(cut).Should().BeFalse();

        button.TriggerEvent("onpointerenter", new PointerEventArgs { PointerType = "mouse" });
        TooltipVisible(cut).Should().BeTrue("hover reveals the full zone");
        button.TriggerEvent("onpointerleave", new PointerEventArgs { PointerType = "mouse" });
        TooltipVisible(cut).Should().BeFalse();

        button.Click();
        TooltipVisible(cut).Should().BeTrue("tap pins the full zone");
        cut.Find("button.chart-zone-token").Click();
        TooltipVisible(cut).Should().BeFalse();
    }

    [Test]
    public async Task ZoneCaption_RecoversAfterAnInitialFormatFailure()
    {
        _ui.Ui.MessageTime = new MessageTimePreferences { Zone = MessageTimePreferences.ZoneLocal };
        _formatter.ZoneLabel = "America/New_York";
        _formatter.ZoneName = "Eastern Standard Time";
        _formatter.Abbreviation = "EST";
        _formatter.OffsetLabel = "UTC-05:00";
        _formatter.FailuresRemaining = 1;
        var series = new ChartSeries { DisplayName = "Temp" };
        _chartData.GetPoints(series.Id).Returns(
        [
            new ChartDataPoint(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc), 1)
        ]);

        var cut = RenderCard(series);
        cut.WaitForAssertion(
            () => _formatter.FailuresRemaining.Should().Be(0),
            TimeSpan.FromSeconds(5));
        cut.FindAll("button.chart-zone-token").Should().BeEmpty("the first formatter call failed");

        await cut.InvokeAsync(() => cut.Instance.Tick());

        cut.WaitForAssertion(
            () => cut.Find("button.chart-zone-token").TextContent.Trim().Should().Be("EST"),
            TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task ZoneCaption_RecoversFromAFailedPreferenceRefreshWhilePaused()
    {
        _ui.Ui.MessageTime = new MessageTimePreferences { Zone = MessageTimePreferences.ZoneLocal };
        _formatter.ZoneLabel = "America/New_York";
        _formatter.ZoneName = "Eastern Standard Time";
        _formatter.Abbreviation = "EST";
        _formatter.OffsetLabel = "UTC-05:00";
        var series = new ChartSeries { DisplayName = "Temp" };
        _chartData.GetPoints(series.Id).Returns(
        [
            new ChartDataPoint(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc), 1)
        ]);

        var cut = RenderCard(series);
        cut.WaitForAssertion(
            () => cut.Find("button.chart-zone-token").TextContent.Trim().Should().Be("EST"),
            TimeSpan.FromSeconds(5));

        cut.Find("button[title='Pause live updates (keeps the view still)']").Click();
        cut.WaitForAssertion(() => SummaryText(cut).Should().Be("Paused"), TimeSpan.FromSeconds(5));

        _formatter.ZoneLabel = "UTC";
        _formatter.ZoneName = "Coordinated Universal Time";
        _formatter.Abbreviation = "UTC";
        _formatter.OffsetLabel = "UTC+00:00";
        _formatter.FailuresRemaining = 1;
        _ui.Ui.MessageTime = new MessageTimePreferences { Zone = MessageTimePreferences.ZoneUtc };
        _ui.UiPreferencesChanged += Raise.Event<Action>();

        cut.WaitForAssertion(
            () => _formatter.FailuresRemaining.Should().Be(0, "the UTC refresh failed once"),
            TimeSpan.FromSeconds(5));

        await cut.InvokeAsync(() => cut.Instance.Tick());

        cut.WaitForAssertion(
            () => cut.Find("button.chart-zone-token").TextContent.Trim().Should().Be("UTC"),
            TimeSpan.FromSeconds(5));

        cut.Find("button[title='Resume live updates']").Click();

        cut.WaitForAssertion(
            () => JSInterop.Invocations["updateChart"].Should().ContainSingle(),
            TimeSpan.FromSeconds(5));
        UpdateSpec().Zone.Should().Be(MessageTimePreferences.ZoneUtc,
            "the deferred preference change reaches the renderer on resume");
    }

    [Test]
    public async Task ZoneCaption_RetriesAreBoundedAndResetByANewTrigger()
    {
        _ui.Ui.MessageTime = new MessageTimePreferences { Zone = MessageTimePreferences.ZoneLocal };
        _formatter.ZoneLabel = "America/New_York";
        _formatter.ZoneName = "Eastern Standard Time";
        _formatter.Abbreviation = "EST";
        _formatter.OffsetLabel = "UTC-05:00";
        _formatter.FailuresRemaining = 100;
        var series = new ChartSeries { DisplayName = "Temp" };
        _chartData.GetPoints(series.Id).Returns(
        [
            new ChartDataPoint(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc), 1)
        ]);

        var cut = RenderCard(series);
        cut.WaitForAssertion(
            () => _formatter.FailuresRemaining.Should().Be(99, "the initial formatter call failed"),
            TimeSpan.FromSeconds(5));

        for (var index = 0; index < 8; index++)
        {
            await cut.InvokeAsync(() => cut.Instance.Tick());
            await cut.InvokeAsync(() => Task.CompletedTask);
        }

        _formatter.Requests.Should().HaveCount(5, "the initial failure plus four bounded tick retries");
        await cut.InvokeAsync(() => cut.Instance.Tick());
        _formatter.Requests.Should().HaveCount(5, "no retry continues after the cap");

        _formatter.ZoneLabel = "UTC";
        _formatter.ZoneName = "Coordinated Universal Time";
        _formatter.Abbreviation = "UTC";
        _formatter.OffsetLabel = "UTC+00:00";
        _formatter.FailuresRemaining = 0;
        _ui.Ui.MessageTime = new MessageTimePreferences { Zone = MessageTimePreferences.ZoneUtc };
        _ui.UiPreferencesChanged += Raise.Event<Action>();

        cut.WaitForAssertion(
            () => cut.Find("button.chart-zone-token").TextContent.Trim().Should().Be("UTC"),
            TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task ZoneCaption_OldInFlightRangeDoesNotReplaceTheLatestViewport()
    {
        var transition = new DateTimeOffset(2026, 11, 1, 6, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        _ui.Ui.MessageTime = new MessageTimePreferences { Zone = MessageTimePreferences.ZoneLocal };
        _formatter.ZoneLabel = "America/New_York";
        _formatter.ItemFor = epoch => epoch < transition
            ? ("EDT", "Eastern Daylight Time", "UTC-04:00")
            : ("EST", "Eastern Standard Time", "UTC-05:00");
        var series = new ChartSeries { DisplayName = "Temp" };
        _chartData.GetPoints(series.Id).Returns(
        [
            new ChartDataPoint(new DateTime(2026, 11, 1, 5, 58, 0, DateTimeKind.Utc), 1),
            new ChartDataPoint(new DateTime(2026, 11, 1, 6, 2, 0, DateTimeKind.Utc), 2)
        ]);

        var cut = RenderCard(series);
        cut.WaitForAssertion(
            () => cut.Find("button.chart-zone-token").TextContent.Trim().Should().Be("EDT / EST"),
            TimeSpan.FromSeconds(5));

        _clock.Advance(TimeSpan.FromSeconds(1));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _formatter.Gate = gate;
        var initialRequests = _formatter.Requests.Count;

        var oldZoom = cut.InvokeAsync(() => cut.Instance.OnEChartsViewportChanged(
            transition - (10 * 60_000), transition - (5 * 60_000)));
        cut.WaitForAssertion(
            () => _formatter.Requests.Should().HaveCount(initialRequests + 1),
            TimeSpan.FromSeconds(5));

        await cut.InvokeAsync(() => cut.Instance.OnEChartsViewportChanged(
            transition + (5 * 60_000), transition + (10 * 60_000)));

        gate.SetResult();
        await oldZoom;

        cut.WaitForAssertion(
            () => cut.Find("button.chart-zone-token").TextContent.Trim().Should().Be("EST"),
            TimeSpan.FromSeconds(5));
        cut.Find("button.chart-zone-token").TextContent.Trim().Should()
            .Be("EST", "the old in-flight range must not replace the latest viewport");
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task ZoneCaption_NonZonePreferenceChangeDuringInFlightRequestKeepsTheZoneCaption(bool changeClockFormat)
    {
        var transition = new DateTimeOffset(2026, 11, 1, 6, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        _ui.Ui.MessageTime = new MessageTimePreferences { Zone = MessageTimePreferences.ZoneLocal };
        _formatter.ZoneLabel = "America/New_York";
        _formatter.ItemFor = epoch => epoch < transition
            ? ("EDT", "Eastern Daylight Time", "UTC-04:00")
            : ("EST", "Eastern Standard Time", "UTC-05:00");
        var series = new ChartSeries { DisplayName = "Temp" };
        _chartData.GetPoints(series.Id).Returns(
        [
            new ChartDataPoint(new DateTime(2026, 11, 1, 5, 58, 0, DateTimeKind.Utc), 1),
            new ChartDataPoint(new DateTime(2026, 11, 1, 6, 2, 0, DateTimeKind.Utc), 2)
        ]);

        var cut = RenderCard(series);
        cut.WaitForAssertion(
            () => cut.Find("button.chart-zone-token").TextContent.Trim().Should().Be("EDT / EST"),
            TimeSpan.FromSeconds(5));

        _clock.Advance(TimeSpan.FromSeconds(1));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _formatter.Gate = gate;
        var initialRequests = _formatter.Requests.Count;

        var zoom = cut.InvokeAsync(() => cut.Instance.OnEChartsViewportChanged(
            transition + (5 * 60_000), transition + (10 * 60_000)));
        cut.WaitForAssertion(
            () => _formatter.Requests.Should().HaveCount(initialRequests + 1),
            TimeSpan.FromSeconds(5));

        _ui.Ui.MessageTime = changeClockFormat
            ? new MessageTimePreferences
            {
                Zone = MessageTimePreferences.ZoneLocal,
                ClockFormat = MessageTimePreferences.ClockFormat12Hour
            }
            : new MessageTimePreferences
            {
                Zone = MessageTimePreferences.ZoneLocal,
                Mode = MessageTimePreferences.ModeElapsed
            };
        _ui.UiPreferencesChanged += Raise.Event<Action>();

        if (changeClockFormat)
        {
            cut.WaitForAssertion(
                () => JSInterop.Invocations["updateChart"].Should().ContainSingle(),
                TimeSpan.FromSeconds(5));
            UpdateSpec().ClockFormat.Should().Be(MessageTimePreferences.ClockFormat12Hour);
        }
        else
        {
            await cut.InvokeAsync(() => Task.CompletedTask);
        }

        gate.SetResult();
        await zoom;

        cut.WaitForAssertion(
            () => cut.Find("button.chart-zone-token").TextContent.Trim().Should().Be("EST"),
            TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task ZoneCaption_SameTargetNotificationsCoalesceWithTheInFlightRequest()
    {
        _ui.Ui.MessageTime = new MessageTimePreferences { Zone = MessageTimePreferences.ZoneLocal };
        _formatter.ZoneLabel = "America/New_York";
        _formatter.ZoneName = "Eastern Standard Time";
        _formatter.Abbreviation = "EST";
        _formatter.OffsetLabel = "UTC-05:00";
        _formatter.BridgeReady = false;
        var series = new ChartSeries { DisplayName = "Temp" };
        _chartData.GetPoints(series.Id).Returns(
        [
            new ChartDataPoint(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc), 1)
        ]);

        var cut = RenderCard(series);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _formatter.Gate = gate;
        _formatter.BridgeReady = true;

        var bridge = cut.InvokeAsync(() => cut.Instance.EnsureTimeBridgeAsync());
        cut.WaitForAssertion(
            () => _formatter.Requests.Should().ContainSingle("the initial caption request is genuinely in flight"),
            TimeSpan.FromSeconds(5));

        for (var index = 0; index < 5; index++)
            _chartData.OnDataUpdated += Raise.Event<Action>();

        _formatter.Requests.Should().ContainSingle(
            "same-target notifications must coalesce with the in-flight request, not invalidate it");

        gate.SetResult();
        await bridge;

        cut.WaitForAssertion(
            () => cut.Find("button.chart-zone-token").TextContent.Trim().Should().Be("EST"),
            TimeSpan.FromSeconds(5));
        _formatter.Requests.Should().ContainSingle("the first valid caption request was not replaced");
    }

    [Test]
    public void ZoneCaption_NoTimeWindow_UsesTheCheapPointBounds()
    {
        var series = new ChartSeries { DisplayName = "Temp" };
        var older = _start.UtcDateTime.AddMinutes(-10);
        var newer = _start.UtcDateTime;
        _chartData.GetPoints(series.Id).Returns([new ChartDataPoint(newer, 1)]);
        (DateTime First, DateTime Last)? bounds = (older, newer);
        _chartData.GetPointBounds(series.Id).Returns(bounds);
        var epochs = new List<long>();
        _formatter.ItemFor = epoch =>
        {
            epochs.Add(epoch);
            return ("UTC", "Coordinated Universal Time", "UTC+00:00");
        };

        var cut = RenderCard(series);

        cut.WaitForAssertion(() => _formatter.Requests.Should().NotBeEmpty(), TimeSpan.FromSeconds(5));
        epochs.Min().Should().Be(Epoch(older), "the caption starts at the buffered first point");
        epochs.Max().Should().Be(Epoch(newer), "the caption ends at the buffered last point");
    }

    [Test]
    public void ZoneCaption_NoTimeWindow_SpansReversedEndpointsAcrossSeries()
    {
        var first = new ChartSeries { DisplayName = "First" };
        var second = new ChartSeries { DisplayName = "Second" };
        var t0 = _start.UtcDateTime.AddMinutes(-30);
        var t1 = _start.UtcDateTime.AddMinutes(-20);
        var t2 = _start.UtcDateTime.AddMinutes(-10);
        var t3 = _start.UtcDateTime;
        _chartData.GetPoints(Arg.Any<Guid>()).Returns([]);
        (DateTime First, DateTime Last)? firstBounds = (t2, t1);
        (DateTime First, DateTime Last)? secondBounds = (t3, t0);
        _chartData.GetPointBounds(first.Id).Returns(firstBounds);
        _chartData.GetPointBounds(second.Id).Returns(secondBounds);
        var epochs = new List<long>();
        _formatter.ItemFor = epoch =>
        {
            epochs.Add(epoch);
            return ("UTC", "Coordinated Universal Time", "UTC+00:00");
        };

        var cut = RenderCard(first, second);

        cut.WaitForAssertion(() => _formatter.Requests.Should().NotBeEmpty(), TimeSpan.FromSeconds(5));
        epochs.Min().Should().Be(Epoch(t0), "a reversed series still contributes its low endpoint");
        epochs.Max().Should().Be(Epoch(t3), "the caption spans every configured series");
    }

    [Test]
    public void ZoneCaption_NoTimeWindow_UnavailableBoundsFallBackToTheBufferedPoints()
    {
        var series = new ChartSeries { DisplayName = "Temp" };
        var buffered = _start.UtcDateTime.AddMinutes(-5);
        _chartData.GetPoints(series.Id).Returns([new ChartDataPoint(buffered, 1)]);
        _chartData.GetPointBounds(series.Id).Returns(((DateTime First, DateTime Last)?)null);
        var epochs = new List<long>();
        _formatter.ItemFor = epoch =>
        {
            epochs.Add(epoch);
            return ("UTC", "Coordinated Universal Time", "UTC+00:00");
        };

        var cut = RenderCard(series);

        cut.WaitForAssertion(() => _formatter.Requests.Should().NotBeEmpty(), TimeSpan.FromSeconds(5));
        epochs.Should().OnlyContain(epoch => epoch == Epoch(buffered),
            "a bound that is not reliably available falls back to the existing buffered points");
    }

    [Test]
    public void ZoneCaption_ConfiguredTimeWindow_KeepsTheFilteredPointsPath()
    {
        var series = new ChartSeries { DisplayName = "Temp" };
        var now = DateTime.UtcNow;
        var inside = now.AddMinutes(-1);
        var outside = now.AddMinutes(-10);
        _chartData.GetPoints(series.Id).Returns([
            new ChartDataPoint(outside, 1),
            new ChartDataPoint(inside, 2)
        ]);
        (DateTime First, DateTime Last)? wideBounds = (now.AddDays(-1), now);
        _chartData.GetPointBounds(series.Id).Returns(wideBounds);
        var epochs = new List<long>();
        _formatter.ItemFor = epoch =>
        {
            epochs.Add(epoch);
            return ("UTC", "Coordinated Universal Time", "UTC+00:00");
        };
        _configuration = new ChartConfiguration { Name = "Chart", TimeWindowMinutes = 5, Series = [series] };

        var cut = Render<ChartCardComponent>(parameters => parameters
            .Add(c => c.Configuration, _configuration)
            .Add(c => c.Clock, _clock));

        cut.WaitForAssertion(() => _formatter.Requests.Should().NotBeEmpty(), TimeSpan.FromSeconds(5));
        _chartData.DidNotReceive().GetPointBounds(Arg.Any<Guid>());
        epochs.Should().OnlyContain(epoch => epoch == Epoch(inside),
            "a configured window keeps the existing inclusive cutoff and source order");
    }
}
