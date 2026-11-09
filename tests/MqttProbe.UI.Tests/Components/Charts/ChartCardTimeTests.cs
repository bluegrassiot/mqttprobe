using System.Text.Json;
using ApexCharts;
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

    [Test]
    public void AxisAndTooltipFormatters_UseTheSelectedLocalTwelveHourClock()
    {
        _ui.Ui.MessageTime = new MessageTimePreferences
        {
            Zone = MessageTimePreferences.ZoneLocal,
            ClockFormat = MessageTimePreferences.ClockFormat12Hour
        };

        var cut = RenderCard(new ChartSeries { DisplayName = "Temp" });
        var options = cut.FindComponent<ApexChart<ChartDataPoint>>().Instance.Options;

        options.Xaxis.Labels.DatetimeUTC.Should().BeFalse("the viewing device zone drives tick placement");
        options.Xaxis.Labels.MinHeight.Should().Be(30, "a two-line date tick needs room for the time line");
        options.Xaxis.Labels.Formatter.Should()
            .Contain("window.mqttProbeTime")
            .And.Contain("\"zone\":\"local\"")
            .And.Contain("\"clockFormat\":\"12h\"")
            .And.Contain("formatAxis");
        options.Tooltip.X.Formatter.Should()
            .Contain("window.mqttProbeTime")
            .And.Contain("\"zone\":\"local\"")
            .And.Contain("\"clockFormat\":\"12h\"")
            .And.Contain("formatTooltip");
        options.Tooltip.X.Format.Should().BeNull("a fixed HH:mm:ss format would ignore the preference");
    }

    [Test]
    public void AxisAndTooltipFormatters_UseUtcAndTheTwentyFourHourClock()
    {
        _ui.Ui.MessageTime = new MessageTimePreferences
        {
            Zone = MessageTimePreferences.ZoneUtc,
            ClockFormat = MessageTimePreferences.ClockFormat24Hour
        };

        var cut = RenderCard(new ChartSeries { DisplayName = "Temp" });
        var options = cut.FindComponent<ApexChart<ChartDataPoint>>().Instance.Options;

        options.Xaxis.Labels.DatetimeUTC.Should().BeTrue("UTC ticks must not shift into the device zone");
        options.Xaxis.Labels.Formatter.Should()
            .Contain("\"zone\":\"utc\"")
            .And.Contain("\"clockFormat\":\"24h\"")
            .And.Contain("formatAxis");
        options.Tooltip.X.Formatter.Should()
            .Contain("\"zone\":\"utc\"")
            .And.Contain("\"clockFormat\":\"24h\"");
    }

    [Test]
    public void AxisFormatter_AddsADateLineAtMidnightAndForMultiDayRanges()
    {
        var axis = ChartTimeFormat.AxisFormatter(new MessageTimePreferences());
        var tooltip = ChartTimeFormat.TooltipFormatter(new MessageTimePreferences());

        axis.Should().StartWith("function(");
        axis.Should().Contain("formatAxis").And.Contain("formatTooltip");
        axis.Should().Contain("maxX").And.Contain("minX");
        axis.Should().Contain("00:00:00");
        axis.Should().Contain("[date, time]");
        tooltip.Should().StartWith("function(");
        tooltip.Should().Contain("Array.isArray(value)");
        tooltip.Should().Contain("formatTooltip");
    }

    [Test]
    public void TooltipFormatter_AcceptsSharedTooltipValues()
    {
        var formatter = ChartTimeFormat.TooltipFormatter(new MessageTimePreferences());

        formatter.Should().StartWith("function(");
        formatter.Should().Contain("Array.isArray(value)");
        formatter.Should().Contain("formatTooltip");
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

        cut.FindComponents<ApexChart<ChartDataPoint>>().Should()
            .BeEmpty("formatter callbacks need the shared time bridge first");
        cut.FindAll(".chart-card-plot__pending").Should().ContainSingle();

        _formatter.BridgeReady = true;
        await cut.InvokeAsync(() => cut.Instance.EnsureTimeBridgeAsync());

        cut.WaitForAssertion(
            () => cut.FindComponents<ApexChart<ChartDataPoint>>().Should().ContainSingle(),
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
        var before = cut.FindComponent<ApexPointSeries<ChartDataPoint>>().Instance;
        var beforeX = before.XValue(point);
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
            var options = cut.FindComponent<ApexChart<ChartDataPoint>>().Instance.Options;
            options.Xaxis.Labels.DatetimeUTC.Should().BeFalse();
            options.Xaxis.Labels.Formatter.Should()
                .Contain("\"zone\":\"local\"")
                .And.Contain("\"clockFormat\":\"12h\"")
                .And.Contain("formatAxis");
            options.Tooltip.X.Formatter.Should().Contain("\"clockFormat\":\"12h\"");
            var update = JSInterop.Invocations.Single(
                invocation => invocation.Identifier == "blazor_apexchart.updateOptions");
            var json = update.Arguments[1] as string;
            json.Should().NotBeNull();

            using var document = JsonDocument.Parse(json!);
            var root = document.RootElement;
            var labels = root.GetProperty("xaxis").GetProperty("labels");
            labels.GetProperty("datetimeUTC").GetBoolean().Should().BeFalse();
            var axisFormatter = labels.GetProperty("formatter").GetProperty("@eval").GetString();
            var tooltipFormatter = root.GetProperty("tooltip").GetProperty("x")
                .GetProperty("formatter").GetProperty("@eval").GetString();
            axisFormatter.Should()
                .Contain("formatAxis")
                .And.Contain("\"zone\":\"local\"")
                .And.Contain("\"clockFormat\":\"12h\"");
            tooltipFormatter.Should()
                .Contain("formatTooltip")
                .And.Contain("\"zone\":\"local\"")
                .And.Contain("\"clockFormat\":\"12h\"");
            cut.Find("button.chart-zone-token").TextContent.Trim().Should().Be("BST");
            cut.Find("button.chart-zone-token").GetAttribute("aria-label").Should().Contain("Europe/London");
        }, TimeSpan.FromSeconds(5));

        cut.FindComponent<ApexPointSeries<ChartDataPoint>>().Instance.XValue(point).Should()
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

        var chart = cut.FindComponent<ApexChart<ChartDataPoint>>().Instance;
        await cut.InvokeAsync(() => chart.OnZoomed.InvokeAsync(new ZoomedData<ChartDataPoint>
        {
            XAxis = new SelectionXAxis
            {
                Min = transition,
                Max = transition + 60_000
            }
        }));

        cut.WaitForAssertion(
            () => cut.Find("button.chart-zone-token").TextContent.Trim().Should().Be("EST"),
            TimeSpan.FromSeconds(5));

        await cut.InvokeAsync(() => chart.OnZoomed.InvokeAsync(new ZoomedData<ChartDataPoint>()));

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

        cut.Find("button[title='Pause live updates (enables zoom/pan/export)']").Click();
        cut.WaitForAssertion(() => SummaryText(cut).Should().Be("Paused"), TimeSpan.FromSeconds(5));

        _formatter.ZoneLabel = "UTC";
        _formatter.ZoneName = "Coordinated Universal Time";
        _formatter.Abbreviation = "UTC";
        _formatter.OffsetLabel = "UTC+00:00";
        _formatter.FailuresRemaining = 1;
        _ui.Ui.MessageTime = new MessageTimePreferences { Zone = MessageTimePreferences.ZoneUtc };
        _ui.UiPreferencesChanged += Raise.Event<Action>();

        cut.WaitForAssertion(
            () => cut.FindComponent<ApexChart<ChartDataPoint>>().Instance.Options.Xaxis.Labels.DatetimeUTC.Should().BeTrue(),
            TimeSpan.FromSeconds(5));
        cut.WaitForAssertion(
            () => _formatter.FailuresRemaining.Should().Be(0, "the UTC refresh failed once"),
            TimeSpan.FromSeconds(5));

        await cut.InvokeAsync(() => cut.Instance.Tick());

        cut.WaitForAssertion(
            () => cut.Find("button.chart-zone-token").TextContent.Trim().Should().Be("UTC"),
            TimeSpan.FromSeconds(5));
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
    public async Task ZoneCaption_ThrottledRangeChangeReplacesTheInFlightOldRange()
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
        var chart = cut.FindComponent<ApexChart<ChartDataPoint>>().Instance;

        var oldZoom = cut.InvokeAsync(() => chart.OnZoomed.InvokeAsync(new ZoomedData<ChartDataPoint>
        {
            XAxis = new SelectionXAxis
            {
                Min = transition - (10 * 60_000),
                Max = transition - (5 * 60_000)
            }
        }));
        cut.WaitForAssertion(
            () => _formatter.Requests.Should().HaveCount(initialRequests + 1),
            TimeSpan.FromSeconds(5));

        await cut.InvokeAsync(() => chart.OnScrolled.InvokeAsync(new SelectionData<ChartDataPoint>
        {
            XAxis = new SelectionXAxis
            {
                Min = transition + (5 * 60_000),
                Max = transition + (10 * 60_000)
            }
        }));

        gate.SetResult();
        await oldZoom;

        cut.Find("button.chart-zone-token").TextContent.Trim().Should()
            .Be("EDT / EST", "the old in-flight range must not replace the latest pan range");

        await cut.InvokeAsync(() => cut.Instance.Tick());

        cut.WaitForAssertion(
            () => cut.Find("button.chart-zone-token").TextContent.Trim().Should().Be("EST"),
            TimeSpan.FromSeconds(5));
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
        var chart = cut.FindComponent<ApexChart<ChartDataPoint>>().Instance;

        var zoom = cut.InvokeAsync(() => chart.OnZoomed.InvokeAsync(new ZoomedData<ChartDataPoint>
        {
            XAxis = new SelectionXAxis
            {
                Min = transition + (5 * 60_000),
                Max = transition + (10 * 60_000)
            }
        }));
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
                () => cut.FindComponent<ApexChart<ChartDataPoint>>().Instance.Options.Tooltip.X.Formatter
                    .Should().Contain("\"clockFormat\":\"12h\""),
                TimeSpan.FromSeconds(5));
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
    public void ChartOptions_KeepRotationAutomaticAndReserveEdgePadding()
    {
        var cut = RenderCard(new ChartSeries { DisplayName = "Temp" });
        var options = cut.FindComponent<ApexChart<ChartDataPoint>>().Instance.Options;

        options.Xaxis.Labels.RotateAlways.Should().BeFalse("45 degrees only when the width needs it");
        options.Xaxis.Labels.HideOverlappingLabels.Should().BeTrue();
        options.Grid.Padding.Should().NotBeNull();
        options.Grid.Padding.Left.Should().BeGreaterThan(0);
        options.Grid.Padding.Right.Should().BeGreaterThan(0);
    }
}

