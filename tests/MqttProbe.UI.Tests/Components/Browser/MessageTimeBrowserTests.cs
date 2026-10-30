using System.Collections.Immutable;
using AngleSharp.Dom;
using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Metrics;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Core.Services.Plugins.BuiltIn;
using MqttProbe.Core.Services.Plugins.Pipeline;
using MqttProbe.Core.Services.Plugins.Registry;
using MqttProbe.UI.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.UI.Tests.Components.Browser;

[TestFixture]
public class MessageTimeBrowserTests : BunitTestContext
{
    private const string ModulePath = "./_content/MqttProbe.UI/js/message-time-format.js";
    private static readonly Guid _storeId = Guid.NewGuid();
    private static readonly DateTime _baseUtc = new(2026, 10, 29, 22, 41, 7, 123, DateTimeKind.Utc);

    private IMessageStoreManager _store = null!;
    private RecordingUiSettings _settings = null!;
    private SelectedTopicState _selection = null!;
    private List<MqttMessage> _messages = [];

    [SetUp]
    public void Setup()
    {
        _store = Substitute.For<IMessageStoreManager>();
        _selection = new SelectedTopicState(new SelectedTopicToken(_storeId, 1, 1), "sensor/temp", 0);
        _store.GetSelectedTopicState().Returns(_ => _selection);
        _store.GetSelectedMessagesAsync(Arg.Any<SelectedTopicToken>(), Arg.Any<int>())
            .Returns(call => Task.FromResult<SelectedMessagesSnapshot?>(new SelectedMessagesSnapshot(
                _selection with { Token = call.ArgAt<SelectedTopicToken>(0) },
                call.ArgAt<int>(1),
                ImmutableArray.CreateRange(_messages))));
        Services.AddSingleton(_store);

        _settings = new RecordingUiSettings();
        Services.AddUiSettings(_settings);

        var performance = Substitute.For<IPerformanceSettings>();
        performance.Performance.Returns(new AppConfiguration().Performance);
        Services.AddPerformanceSettings(performance);

        Services.AddSingleton(Substitute.For<IUxMetricsService>());
        Services.AddSingleton(Substitute.For<IDialogService>());
        Services.AddSingleton(Substitute.For<ISnackbar>());

        var builder = new PluginRegistryBuilder();
        BuiltInPluginRegistration.RegisterBuiltIns(builder);
        var pipeline = new PayloadPipeline(builder.Build(), Substitute.For<Microsoft.Extensions.Logging.ILogger<PayloadPipeline>>());
        Services.AddSingleton<IFormatDisplayNames>(new FormatDisplayNames(pipeline));
    }

    private static MqttMessage Message(
        string payload,
        int offsetMilliseconds = 0,
        TimeSpan? elapsed = null,
        TimeSpan? previousAll = null,
        TimeSpan? previousTopic = null) =>
        new()
        {
            Topic = "sensor/temp",
            Payload = payload,
            DateTimeReceived = _baseUtc.AddMilliseconds(offsetMilliseconds),
            ElapsedSinceBufferStart = elapsed,
            SincePreviousAllTopics = previousAll,
            SincePreviousSameTopic = previousTopic
        };

    private (IRenderedComponent<MudPopoverProvider> Provider, IRenderedComponent<PayloadBrowser> Browser) RenderBrowser()
    {
        var provider = Render<MudPopoverProvider>();
        var browser = Render<PayloadBrowser>();
        browser.WaitForAssertion(() => browser.FindAll(".payload-row").Should().HaveCount(_messages.Count));
        return (provider, browser);
    }

    private static string Text(IElement element) =>
        string.Join(' ', element.TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string AccessibleName(IElement element)
    {
        var parts = new List<string>();
        Collect(element);
        return string.Join(' ', string.Concat(parts).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        void Collect(INode node)
        {
            foreach (var child in node.ChildNodes)
            {
                if (child is IElement childElement && childElement.GetAttribute("aria-hidden") == "true")
                    continue;
                if (child is IElement { LocalName: "svg" })
                    continue;
                if (child is IText text)
                    parts.Add(text.Data);
                else
                    Collect(child);
            }
        }
    }

    private static IReadOnlyList<string> RowTimes(IRenderedComponent<PayloadBrowser> browser) =>
        browser.FindAll("[data-time-value]").Select(Text).ToList();

    private static IElement Option(IRenderedComponent<MudPopoverProvider> provider, string attribute, string value) =>
        provider.FindAll($"[{attribute}='{value}']").Should().ContainSingle().Subject;

    private static void OpenTimeMenu(IRenderedComponent<PayloadBrowser> browser, IRenderedComponent<MudPopoverProvider> provider)
    {
        browser.Find(".pb-time-trigger").Click();
        provider.WaitForAssertion(() => provider.FindAll("[data-time-menu]").Should().ContainSingle());
    }

    [Test]
    public void DefaultDisplay_IsUtc24WithOneHeaderZoneAndNoRowZone()
    {
        _messages = [Message("first"), Message("second", 1_000), Message("third", 65_500)];

        var (_, browser) = RenderBrowser();

        RowTimes(browser).Should().Equal("22:41:07.123", "22:41:08.123", "22:42:12.623");
        Text(browser.Find("[data-time-header-meta]")).Should().Be("UTC");
        browser.FindAll("[data-time-header-meta]").Should().ContainSingle();
        browser.FindAll("[data-time-value]").Should().OnlyContain(row => !Text(row).Contains("UTC"));
    }

    [Test]
    public void Details_ShowReceivedZoneOffsetAndFullZoneName()
    {
        _messages = [Message("first")];
        var (_, browser) = RenderBrowser();

        browser.FindAll(".payload-row")[0].Click();

        Text(browser.Find(".pb-received-instant")).Should().Be("2026-10-29 22:41:07.123");
        Text(browser.Find("[data-received-zone]")).Should().Be("UTC");
        Text(browser.Find(".pb-received-zone-name")).Should().Be("Coordinated Universal Time · UTC+00:00");
    }

    [Test]
    public void TwelveHourPreference_KeepsMillisecondsAndMatchesTheDetails()
    {
        _settings.Ui.MessageTime = new MessageTimePreferences { ClockFormat = MessageTimePreferences.ClockFormat12Hour };
        _messages = [Message("first")];

        var (_, browser) = RenderBrowser();

        var time = RowTimes(browser).Should().ContainSingle().Subject;
        time.Should().MatchRegex(@"^\d{2}:\d{2}:\d{2}\.\d{3} (AM|PM)$");

        browser.FindAll(".payload-row")[0].Click();

        Text(browser.Find(".pb-received-instant")).Should().Be($"2026-10-29 {time}");
    }

    [Test]
    public void TimeMenu_ChangesPersistAndConditionalScopeAppearsForDelta()
    {
        _messages = [Message("first", 0, previousAll: null, previousTopic: null), Message("second", 1_250, previousAll: TimeSpan.FromMilliseconds(1_250))];
        var (provider, browser) = RenderBrowser();

        OpenTimeMenu(browser, provider);
        Option(provider, "data-time-format", "12h").Click();

        browser.WaitForAssertion(() => RowTimes(browser)[1].Should().Contain("PM"));
        _settings.SavedMessageTimes.Should().ContainSingle()
            .Which.ClockFormat.Should().Be(MessageTimePreferences.ClockFormat12Hour);

        Option(provider, "data-time-zone", "local").Click();
        provider.WaitForAssertion(() => _settings.SavedMessageTimes.Should().HaveCount(2));
        _settings.SavedMessageTimes[^1].Zone.Should().Be(MessageTimePreferences.ZoneLocal);

        Option(provider, "data-time-mode", "delta").Click();
        provider.WaitForAssertion(() => provider.FindAll("[data-delta-scope]").Should().HaveCount(2));
        provider.FindAll("[data-time-zone]").Should().BeEmpty("zone is a clock-only setting");
        provider.FindAll("[data-time-format]").Should().BeEmpty("clock format is a clock-only setting");

        Option(provider, "data-delta-scope", "topic").Click();
        browser.WaitForAssertion(() => Text(browser.Find("[data-time-header-meta]")).Should().Be("same topic"));
        _settings.SavedMessageTimes[^1].DeltaScope.Should().Be(MessageTimePreferences.DeltaScopeTopic);
        _settings.SavedMessageTimes[^1].Mode.Should().Be(MessageTimePreferences.ModeDelta);
    }

    [Test]
    public void ElapsedMode_ShowsZeroFirstAndUnambiguousDurations_WithAbsoluteTimeInDetails()
    {
        _settings.Ui.MessageTime = new MessageTimePreferences { Mode = MessageTimePreferences.ModeElapsed };
        _messages =
        [
            Message("first", elapsed: TimeSpan.Zero),
            Message("second", 1_000, elapsed: TimeSpan.FromSeconds(65.5)),
            Message("third", 2_000, elapsed: TimeSpan.FromHours(2) + TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(4.005)),
            Message("fourth", 3_000, elapsed: TimeSpan.FromHours(26))
        ];

        var (_, browser) = RenderBrowser();

        RowTimes(browser).Should().Equal("0 ms", "1m 05.500s", "2h 03m 04.005s", "1d 02h 00m 00.000s");
        Text(browser.Find("[data-time-header-meta]")).Should().Be("from first message");

        browser.FindAll(".payload-row")[1].Click();
        Text(browser.Find(".pb-received-instant")).Should().Be("2026-10-29 22:41:08.123");
    }

    [Test]
    public void ClockFormatAndZone_DoNotChangeElapsedDurations()
    {
        _settings.Ui.MessageTime = new MessageTimePreferences
        {
            Mode = MessageTimePreferences.ModeElapsed,
            Zone = MessageTimePreferences.ZoneLocal,
            ClockFormat = MessageTimePreferences.ClockFormat12Hour
        };
        _messages =
        [
            Message("first", elapsed: TimeSpan.Zero),
            Message("second", 1_000, elapsed: TimeSpan.FromSeconds(65.5))
        ];

        var (_, browser) = RenderBrowser();

        RowTimes(browser).Should().Equal("0 ms", "1m 05.500s");
    }

    [Test]
    public void DeltaMode_UsesAnEmptyMarkerForNoPredecessor_AndScopeSwitchesTheStoredInterval()
    {
        _settings.Ui.MessageTime = new MessageTimePreferences { Mode = MessageTimePreferences.ModeDelta };
        _messages =
        [
            Message("first", 0, previousAll: null, previousTopic: null),
            Message("second", 1_250, previousAll: TimeSpan.FromMilliseconds(1_250), previousTopic: null),
            Message("third", 3_000, previousAll: TimeSpan.FromMilliseconds(1_750), previousTopic: TimeSpan.FromMilliseconds(1_750))
        ];

        var (provider, browser) = RenderBrowser();

        RowTimes(browser).Should().Equal("—", "1.250 s", "1.750 s");
        Text(browser.Find("[data-time-header-meta]")).Should().Be("all topics");
        browser.FindAll("[data-time-value]")[0].GetAttribute("aria-label").Should().Be("No previous message");

        OpenTimeMenu(browser, provider);
        Option(provider, "data-delta-scope", "topic").Click();

        browser.WaitForAssertion(() => RowTimes(browser).Should().Equal("—", "—", "1.750 s"));
        Text(browser.Find("[data-time-header-meta]")).Should().Be("same topic");

        Option(provider, "data-delta-scope", "all").Click();
        browser.WaitForAssertion(() => RowTimes(browser)[1].Should().Be("1.250 s"));
    }

    [Test]
    public void BrowserFormatting_ReplacesPendingRowsAndFeedsTheDetails()
    {
        _settings.Ui.MessageTime = new MessageTimePreferences
        {
            Zone = MessageTimePreferences.ZoneLocal,
            ClockFormat = MessageTimePreferences.ClockFormat12Hour
        };
        _messages = [Message("first")];
        var message = _messages[0];

        var module = JSInterop.SetupModule(ModulePath);
        module.Setup<MessageTimeFormatResult?>("formatMessages", _ => true).SetResult(new MessageTimeFormatResult(
            "America/New_York",
            "Eastern Daylight Time",
            [
                new MessageTimeFormatItem(
                    message.Id,
                    "06:41:07.123 PM",
                    "2026-10-29",
                    "2026-10-29 06:41:07.123 PM EDT",
                    "EDT",
                    "Eastern Daylight Time",
                    "UTC-04:00")
            ]));

        var (_, browser) = RenderBrowser();

        browser.WaitForAssertion(() => RowTimes(browser).Should().Equal("06:41:07.123 PM"));
        Text(browser.Find("[data-time-header-meta]")).Should().Be("America/New_York");
        module.Invocations.Count(invocation => invocation.Identifier == "formatMessages")
            .Should().Be(1, "one batch formats the visible rows and must not loop on its own render");

        browser.FindAll(".payload-row")[0].Click();

        Text(browser.Find(".pb-received-instant")).Should().Be("2026-10-29 06:41:07.123 PM");
        Text(browser.Find("[data-received-zone]")).Should().Be("EDT");
        Text(browser.Find(".pb-received-zone-name")).Should().Be("Eastern Daylight Time · UTC-04:00");
    }

    [Test]
    public void LocalBeforeTheBrowserAnswers_ShowsAPendingMarkerRatherThanUtc()
    {
        _settings.Ui.MessageTime = new MessageTimePreferences { Zone = MessageTimePreferences.ZoneLocal };
        _messages = [Message("first")];

        var (_, browser) = RenderBrowser();

        RowTimes(browser).Should().Equal(MessageTimeDisplay.PendingMarker);
        browser.Find("[data-time-value]").GetAttribute("data-time-pending").Should().Be("true");
        browser.Markup.Should().NotContain("22:41:07.123");
        Text(browser.Find("[data-time-header-meta]")).Should().Be("Pending");
        browser.Find("[data-time-header-meta]").GetAttribute("title")
            .Should().Contain("Waiting for this device's time zone");

        browser.FindAll(".payload-row")[0].Click();

        browser.Find("[data-time-pending]").TextContent.Trim().Should().Be(MessageTimeDisplay.PendingMarker);
        Text(browser.Find(".pb-received-zone-name")).Should().Be("Waiting for local time");
        browser.Markup.Should().NotContain("22:41:07.123");
    }

    [Test]
    public void SustainedMessageUpdates_DoNotStarveLocalFormatting()
    {
        _settings.Ui.MessageTime = new MessageTimePreferences { Zone = MessageTimePreferences.ZoneLocal };
        var formatter = new DelayedMessageTimeFormatter();
        Services.AddScoped<IMessageTimeFormatter>(_ => formatter);

        var first = Message("first");
        var second = Message("second", 1_000);
        var third = Message("third", 2_000);
        _messages = [first];
        var (_, browser) = RenderBrowser();

        browser.WaitForAssertion(() => formatter.Calls.Should().ContainSingle());
        formatter.Calls[0].Items.Should().ContainSingle();

        _messages = [first, second];
        AdvanceSelection();
        browser.WaitForAssertion(() => browser.FindAll(".payload-row").Should().HaveCount(2), TimeSpan.FromSeconds(5));
        formatter.Calls.Should().ContainSingle("only one formatting batch may be in flight");

        formatter.Calls[0].Completion.SetResult(LocalResult((first, "09:41:07.123 AM")));
        browser.WaitForAssertion(() => RowTimes(browser)[0].Should().Be("09:41:07.123 AM"));
        Text(browser.Find("[data-time-header-meta]")).Should().Be("Asia/Tokyo");
        browser.WaitForAssertion(() => formatter.Calls.Should().HaveCount(2));
        formatter.Calls[1].Items.Select(item => item.Key).Should().Equal(first.Id, second.Id);

        _messages = [first, second, third];
        AdvanceSelection();
        browser.WaitForAssertion(() => browser.FindAll(".payload-row").Should().HaveCount(3), TimeSpan.FromSeconds(5));
        formatter.Calls.Should().HaveCount(2);
        RowTimes(browser)[2].Should().Be(MessageTimeDisplay.PendingMarker, "a new local row must not expose UTC");
        browser.FindAll("[data-time-value]")[2].GetAttribute("data-time-pending").Should().Be("true");
        browser.Markup.Should().NotContain("22:41:09.123");

        formatter.Calls[1].Completion.SetResult(LocalResult((first, "09:41:07.123 AM"), (second, "09:41:08.123 AM")));
        browser.WaitForAssertion(() => RowTimes(browser)[1].Should().Be("09:41:08.123 AM"));
        browser.WaitForAssertion(() => formatter.Calls.Should().HaveCount(3));
        formatter.Calls[2].Items.Select(item => item.Key).Should().Equal(first.Id, second.Id, third.Id);
        RowTimes(browser)[2].Should().Be(MessageTimeDisplay.PendingMarker, "the newest message waits for the next batch");
        browser.Markup.Should().NotContain("22:41:09.123");

        formatter.Calls[2].Completion.SetResult(LocalResult(
            (first, "09:41:07.123 AM"),
            (second, "09:41:08.123 AM"),
            (third, "09:41:09.123 AM")));
        browser.WaitForAssertion(() => RowTimes(browser).Should().Equal(
            "09:41:07.123 AM", "09:41:08.123 AM", "09:41:09.123 AM"));
    }

    [Test]
    public void SelectedDetailsStayPendingUntilTheBatchCarriesTheirLocalZone()
    {
        _settings.Ui.MessageTime = new MessageTimePreferences { Zone = MessageTimePreferences.ZoneLocal };
        var formatter = new DelayedMessageTimeFormatter();
        Services.AddScoped<IMessageTimeFormatter>(_ => formatter);
        var message = Message("first");
        _messages = [message];

        var (_, browser) = RenderBrowser();
        browser.WaitForAssertion(() => formatter.Calls.Should().ContainSingle());

        browser.FindAll(".payload-row")[0].Click();
        browser.Find("[data-time-pending]").TextContent.Trim().Should().Be(MessageTimeDisplay.PendingMarker);
        Text(browser.Find(".pb-received-zone-name")).Should().Be("Waiting for local time");
        browser.Markup.Should().NotContain("22:41:07.123");

        formatter.Calls[0].Completion.SetResult(LocalResult((message, "09:41:07.123 AM")));
        browser.WaitForAssertion(() => formatter.Calls.Should().HaveCount(2));
        browser.Find("[data-time-pending]").TextContent.Trim().Should().Be(MessageTimeDisplay.PendingMarker);

        formatter.Calls[1].Completion.SetResult(new MessageTimeFormatResult(
            "Asia/Tokyo",
            "Japan Standard Time",
            [new MessageTimeFormatItem(
                message.Id,
                "09:41:07.123 AM",
                "2026-10-30",
                "2026-10-30 09:41:07.123 AM GMT+9",
                "GMT+9",
                "Japan Standard Time",
                "UTC+09:00")]));
        browser.WaitForAssertion(() => Text(browser.Find(".pb-received-instant"))
            .Should().Be("2026-10-30 09:41:07.123 AM"));
        Text(browser.Find("[data-received-zone]")).Should().Be("GMT+9");
        Text(browser.Find(".pb-received-zone-name")).Should().Be("Japan Standard Time · UTC+09:00");
        browser.FindAll("[data-time-pending]").Should().BeEmpty();
    }

    [Test]
    public void TimeMenuActivator_ExposesTheFullAccessibleName_AndOpensFromTheKeyboard()
    {
        _messages = [Message("first")];
        var (provider, browser) = RenderBrowser();

        var activator = browser.Find(".mud-menu-activator");
        activator.GetAttribute("role").Should().Be("button");
        activator.GetAttribute("tabindex").Should().Be("0");
        activator.GetAttribute("aria-haspopup").Should().Be("menu");
        AccessibleName(activator).Should().Be("Time display settings");
        activator.QuerySelectorAll("button,[role='button']").Should()
            .BeEmpty("the menu activator must not nest another button");

        activator.KeyDown("Enter");

        provider.WaitForAssertion(() => provider.FindAll("[data-time-menu]").Should().ContainSingle());
    }

    [Test]
    public void TimeMenu_EscapeFromTheActivatorOrPanel_ClosesTheMenu()
    {
        _messages = [Message("first")];
        var (provider, browser) = RenderBrowser();

        browser.Find(".pb-time-menu-host").Should().NotBeNull();
        browser.Find("[data-time-trigger]").Click();
        provider.WaitForAssertion(() => provider.FindAll("[data-time-menu]").Should().ContainSingle());

        browser.Find(".mud-menu-activator").KeyDown("Escape");
        provider.WaitForAssertion(() => provider.FindAll("[data-time-menu]").Should().BeEmpty());
        browser.Find(".mud-menu-activator").GetAttribute("aria-expanded").Should().Be("false");

        browser.Find("[data-time-trigger]").Click();
        provider.WaitForAssertion(() => provider.FindAll("[data-time-menu]").Should().ContainSingle());

        provider.Find("[data-time-menu]").KeyDown("Escape");
        provider.WaitForAssertion(() => provider.FindAll("[data-time-menu]").Should().BeEmpty());
        browser.Find(".mud-menu-activator").GetAttribute("aria-expanded").Should().Be("false");

        browser.Find("[data-time-trigger]").Click();
        provider.WaitForAssertion(() => provider.FindAll("[data-time-menu]").Should().ContainSingle());

        provider.Find("[data-time-close]").Click();
        provider.WaitForAssertion(() => provider.FindAll("[data-time-menu]").Should().BeEmpty());
        browser.Find(".mud-menu-activator").GetAttribute("aria-expanded").Should().Be("false");
    }

    [Test]
    public async Task PreferenceChangeDuringABatch_DiscardsTheOldZoneAndStartsAFreshRequest()
    {
        _settings.Ui.MessageTime = new MessageTimePreferences { Zone = MessageTimePreferences.ZoneLocal };
        var formatter = new DelayedMessageTimeFormatter();
        Services.AddScoped<IMessageTimeFormatter>(_ => formatter);
        var message = Message("first");
        _messages = [message];

        var (_, browser) = RenderBrowser();
        browser.WaitForAssertion(() => formatter.Calls.Should().ContainSingle());

        await _settings.SetMessageTimeAsync(new MessageTimePreferences
        {
            Zone = MessageTimePreferences.ZoneUtc,
            ClockFormat = MessageTimePreferences.ClockFormat12Hour
        });
        browser.WaitForAssertion(() => RowTimes(browser)[0].Should().Be("10:41:07.123 PM"));
        formatter.Calls.Should().ContainSingle("the older batch still owns the formatter until it completes");

        formatter.Calls[0].Completion.SetResult(LocalResult((message, "09:41:07.123 AM")));
        browser.WaitForAssertion(() => formatter.Calls.Should().HaveCount(2));
        formatter.Calls[1].Preferences.Zone.Should().Be(MessageTimePreferences.ZoneUtc);
        formatter.Calls[1].Preferences.ClockFormat.Should().Be(MessageTimePreferences.ClockFormat12Hour);
        RowTimes(browser).Should().Equal("10:41:07.123 PM");

        formatter.Calls[1].Completion.SetResult(new MessageTimeFormatResult(
            "UTC",
            "Coordinated Universal Time",
            [new MessageTimeFormatItem(message.Id, "10:41:07.123 PM", "2026-10-29", "2026-10-29 10:41:07.123 PM UTC", "UTC", "Coordinated Universal Time", "UTC+00:00")]));
        browser.WaitForAssertion(() => RowTimes(browser).Should().Equal("10:41:07.123 PM"));
    }

    [Test]
    public void TimeMenu_HintsFollowModeZoneAndScope()
    {
        _messages = [Message("first")];
        var (provider, browser) = RenderBrowser();
        OpenTimeMenu(browser, provider);

        Text(provider.Find(".pb-time-hint")).Should().Be("Clock shows when each message arrived.");

        Option(provider, "data-time-zone", "local").Click();
        provider.WaitForAssertion(() =>
            Text(provider.Find(".pb-time-hint")).Should().Be("Local uses this device's time zone."));

        Option(provider, "data-time-mode", "elapsed").Click();
        provider.WaitForAssertion(() =>
            Text(provider.Find(".pb-time-hint")).Should().Be("Elapsed starts at the first message in the buffer."));

        Option(provider, "data-time-mode", "delta").Click();
        provider.WaitForAssertion(() =>
            Text(provider.Find(".pb-time-hint")).Should().Be(
                "Delta compares with the previous message in receive order."));
    }

    [Test]
    public void TimeMenu_AppliesTheLatestStoredSettingsForEachAction()
    {
        _messages = [Message("first")];
        var (provider, browser) = RenderBrowser();
        _settings.Ui.MessageTime = new MessageTimePreferences
        {
            ClockFormat = MessageTimePreferences.ClockFormat12Hour
        };

        OpenTimeMenu(browser, provider);
        Option(provider, "data-time-zone", "local").Click();

        provider.WaitForAssertion(() => _settings.SavedMessageTimes.Should().ContainSingle());
        var saved = _settings.SavedMessageTimes[0];
        saved.Zone.Should().Be(MessageTimePreferences.ZoneLocal);
        saved.ClockFormat.Should().Be(MessageTimePreferences.ClockFormat12Hour,
            "the menu must start from the latest stored settings, not its rendered snapshot");
    }

    private void AdvanceSelection() =>
        _selection = _selection with { Token = new SelectedTopicToken(_storeId, 1, _selection.Token.ContentVersion + 1) };

    private static MessageTimeFormatResult LocalResult(params (MqttMessage Message, string Time)[] items) =>
        new(
            "Asia/Tokyo",
            "Japan Standard Time",
            items.Select(item => new MessageTimeFormatItem(
                item.Message.Id,
                item.Time,
                "2026-10-30",
                $"2026-10-30 {item.Time} GMT+9",
                "GMT+9",
                "",
                "")).ToList());

    private sealed class DelayedMessageTimeFormatter : IMessageTimeFormatter
    {
        internal sealed record Call(
            List<MessageTimeFormatRequest> Items,
            MessageTimePreferences Preferences,
            TaskCompletionSource<MessageTimeFormatResult?> Completion);

        public List<Call> Calls { get; } = [];

        public Task<MessageTimeFormatResult?> FormatAsync(
            IReadOnlyList<MessageTimeFormatRequest> items,
            MessageTimePreferences preferences)
        {
            var completion = new TaskCompletionSource<MessageTimeFormatResult?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Calls.Add(new Call(items.ToList(), preferences, completion));
            return completion.Task;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
