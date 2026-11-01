using AngleSharp.Dom;
using Microsoft.AspNetCore.Components.Web;
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
public class PayloadBrowserKeyboardTests : BunitTestContext
{
    private static readonly Guid _storeId = Guid.NewGuid();

    private IMessageStoreManager _store = null!;
    private IUxMetricsService _metrics = null!;
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
                System.Collections.Immutable.ImmutableArray.CreateRange(_messages))));
        Services.AddSingleton(_store);

        var performance = Substitute.For<IPerformanceSettings>();
        performance.Performance.Returns(new AppConfiguration().Performance);
        Services.AddPerformanceSettings(performance);

        _metrics = Substitute.For<IUxMetricsService>();
        Services.AddSingleton(_metrics);
        Services.AddUiSettings(SettingsSubstituteRegistration.CreateUiSettings());
        Services.AddSingleton(Substitute.For<IDialogService>());
        Services.AddSingleton(Substitute.For<ISnackbar>());

        var builder = new PluginRegistryBuilder();
        BuiltInPluginRegistration.RegisterBuiltIns(builder);
        var pipeline = new PayloadPipeline(builder.Build(), Substitute.For<Microsoft.Extensions.Logging.ILogger<PayloadPipeline>>());
        Services.AddSingleton<IFormatDisplayNames>(new FormatDisplayNames(pipeline));

        _messages = Payloads("first", "second", "third");
        EnsureMudProviders();
    }

    private static List<MqttMessage> Payloads(params string[] payloads) =>
        payloads.Select(payload => new MqttMessage { Topic = "sensor/temp", Payload = payload }).ToList();

    private static SelectedTopicState Selection(string? key, long generation) =>
        new(new SelectedTopicToken(_storeId, generation, 1), key, 0);

    private IRenderedComponent<PayloadBrowser> RenderList(int expectedRows = 3)
    {
        var cut = Render<PayloadBrowser>();
        cut.WaitForAssertion(() => cut.FindAll(".payload-row").Should().HaveCount(expectedRows));
        return cut;
    }

    private static IElement List(IRenderedComponent<PayloadBrowser> cut) => cut.Find(".payload-message-list");

    private static IElement FocusedList(IRenderedComponent<PayloadBrowser> cut)
    {
        var list = cut.Find(".payload-message-list");
        list.Focus();
        return list;
    }

    private static IReadOnlyList<string> ActiveRowText(IRenderedComponent<PayloadBrowser> cut) =>
        cut.FindAll(".payload-row--active").Select(row => row.TextContent).ToList();

    [Test]
    public void ClickingAMessageRow_StillOpensTheMessageDetail()
    {
        var cut = RenderList();

        cut.FindAll(".payload-row")[1].Click();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Message Details");
            cut.Find(".payload-detail-body").TextContent.Should().Contain("second");
        });
    }

    [Test]
    public void FilteredList_WalksOnlyMatchingMessages()
    {
        var cut = RenderList();
        List(cut).QuerySelector("input")!.Input("third");
        cut.WaitForAssertion(() => cut.FindAll(".payload-row").Should().ContainSingle());

        FocusedList(cut).KeyDown("End");
        ActiveRowText(cut).Should().ContainSingle().Which.Should().Contain("third");

        FocusedList(cut).KeyDown("Enter");
        cut.Find(".payload-detail-body").TextContent.Should().Contain("third");
    }

    [Test]
    public void ActiveMessage_IsExposedToAssistiveTechnology()
    {
        var cut = RenderList();

        FocusedList(cut).KeyDown("ArrowDown");

        var activeId = List(cut).GetAttribute("aria-activedescendant");
        activeId.Should().NotBeNullOrWhiteSpace();
        cut.Find($"#{activeId}").GetAttribute("data-message-index").Should().Be("0");
    }

    [Test]
    public void MessageList_IsKeyboardFocusable()
    {
        var cut = RenderList();

        List(cut).GetAttribute("tabindex").Should().Be("0");
        List(cut).GetAttribute("aria-label").Should().Be("Message history");
    }

    [Test]
    public void TimeSort_IsANativeButtonThatReflectsTheCurrentOrder()
    {
        var cut = RenderList();

        var sort = cut.Find("button.sortable-th");
        sort.GetAttribute("type").Should().Be("button");
        sort.GetAttribute("aria-label").Should().Be("Sort messages by time");
        sort.GetAttribute("title").Should().Be("Sort: oldest first");
        cut.Find("thead th").GetAttribute("aria-sort").Should().Be("descending");
        cut.FindAll(".payload-row")[0].TextContent.Should().Contain("first");

        sort.Click();

        cut.WaitForAssertion(() => cut.FindAll(".payload-row")[0].TextContent.Should().Contain("third"));
        cut.Find("button.sortable-th").GetAttribute("title").Should().Be("Sort: newest first");
        cut.Find("thead th").GetAttribute("aria-sort").Should().Be("ascending");
    }

    [Test]
    public void End_WhileTheNewTopicIsStillPending_DoesNotActivateTheOldTopicMessage()
    {
        _selection = Selection("root", generation: 1);
        _messages = Payloads("root-only");
        var cut = RenderList(1);

        _selection = Selection("/long80", generation: 2);
        _messages = Payloads("long-1", "long-2", "long-3");

        FocusedList(cut).KeyDown("End");

        ActiveRowText(cut).Should().BeEmpty();
        List(cut).GetAttribute("aria-activedescendant").Should().BeNull();

        // The history still reports the topic it actually applied, not the one just requested.
        List(cut).GetAttribute("data-applied-topic-key").Should().Be("root");

        cut.WaitForAssertion(
            () => List(cut).GetAttribute("data-applied-topic-key").Should().Be("/long80"),
            TimeSpan.FromSeconds(5));
        cut.WaitForAssertion(() => cut.FindAll(".payload-row").Should().HaveCount(3), TimeSpan.FromSeconds(5));
    }

    [Test]
    public void ArrowKeys_OnlyNavigateWhileTheListItselfHasFocus()
    {
        var cut = RenderList();
        var list = List(cut);

        list.Focus();
        list.KeyDown("ArrowDown");
        ActiveRowText(cut).Should().ContainSingle().Which.Should().Contain("first");

        // Focus moves into a nested control, such as the message search field.
        list.Blur();
        list.KeyDown("ArrowDown");

        ActiveRowText(cut).Should().ContainSingle().Which.Should().Contain("first");
    }

    [Test]
    public void ArrowDown_MarksTheFirstMessageAsActive()
    {
        var cut = RenderList();

        FocusedList(cut).KeyDown("ArrowDown");

        ActiveRowText(cut).Should().ContainSingle().Which.Should().Contain("first");
    }

    [Test]
    public void ArrowDown_ThenArrowDown_MovesToTheSecondMessage()
    {
        var cut = RenderList();

        FocusedList(cut).KeyDown("ArrowDown");
        FocusedList(cut).KeyDown("ArrowDown");

        ActiveRowText(cut).Should().ContainSingle().Which.Should().Contain("second");
    }

    [Test]
    public void ArrowUp_AtTheFirstMessage_StaysOnTheFirstMessage()
    {
        var cut = RenderList();

        FocusedList(cut).KeyDown("ArrowDown");
        FocusedList(cut).KeyDown("ArrowUp");

        ActiveRowText(cut).Should().ContainSingle().Which.Should().Contain("first");
    }

    [Test]
    public void End_ThenHome_MoveBetweenTheLastAndFirstMessage()
    {
        var cut = RenderList();

        FocusedList(cut).KeyDown("End");
        ActiveRowText(cut).Should().ContainSingle().Which.Should().Contain("third");

        FocusedList(cut).KeyDown("Home");
        ActiveRowText(cut).Should().ContainSingle().Which.Should().Contain("first");
    }

    [Test]
    public void ArrowKeys_DuringImeComposition_DoNotMoveTheCursor()
    {
        var cut = RenderList();
        var list = FocusedList(cut);

        list.KeyDown(new KeyboardEventArgs { Key = "ArrowDown", IsComposing = true });

        ActiveRowText(cut).Should().BeEmpty();
    }

    [TestCase("Enter")]
    [TestCase(" ")]
    public void AutoRepeatedOpenKey_DoesNotOpenTheActiveMessage(string key)
    {
        var cut = RenderList();
        var list = FocusedList(cut);
        list.KeyDown("ArrowDown");

        list.KeyDown(new KeyboardEventArgs { Key = key, Repeat = true });

        cut.Markup.Should().NotContain("Message Details");
    }

    [Test]
    public void Enter_OpensTheActiveMessage()
    {
        var cut = RenderList();

        FocusedList(cut).KeyDown("ArrowDown");
        FocusedList(cut).KeyDown("ArrowDown");
        cut.WaitForAssertion(() => ActiveRowText(cut).Should().ContainSingle()
            .Which.Should().Contain("second"));
        FocusedList(cut).KeyDown("Enter");

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Message Details");
            cut.Find(".payload-detail-body").TextContent.Should().Contain("second");
        });
    }

    [Test]
    public void Space_OpensTheActiveMessage()
    {
        var cut = RenderList();

        FocusedList(cut).KeyDown("End");
        cut.WaitForAssertion(() => ActiveRowText(cut).Should().ContainSingle()
            .Which.Should().Contain("third"));
        FocusedList(cut).KeyDown(" ");

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Message Details");
            cut.Find(".payload-detail-body").TextContent.Should().Contain("third");
        });
    }

    [TestCase("Control")]
    [TestCase("Alt")]
    [TestCase("Meta")]
    public void ModifiedArrow_DoesNotMoveTheActiveMessage(string modifier)
    {
        var cut = RenderList();
        var args = new KeyboardEventArgs
        {
            Key = "ArrowDown",
            CtrlKey = modifier == "Control",
            AltKey = modifier == "Alt",
            MetaKey = modifier == "Meta"
        };

        FocusedList(cut).KeyDown(args);

        ActiveRowText(cut).Should().BeEmpty();
        cut.Markup.Should().NotContain("Message Details");
    }

    [Test]
    public void ArrowKeyFromTheSearchField_DoesNotMoveTheActiveMessage()
    {
        var cut = RenderList();

        List(cut).QuerySelector("input")!.KeyDown("ArrowDown");

        ActiveRowText(cut).Should().BeEmpty();
    }

    [Test]
    public void ArrowDown_WhenNoMessages_DoesNothing()
    {
        _messages = [];
        var cut = Render<PayloadBrowser>();
        cut.WaitForAssertion(() => cut.FindAll(".payload-row").Should().BeEmpty());

        var act = () => FocusedList(cut).KeyDown("ArrowDown");

        act.Should().NotThrow();
        cut.Markup.Should().NotContain("Message Details");
    }

    [Test]
    public void ActiveMessage_KeepsItsRow_WhenLiveContentReplacesTheMessages()
    {
        var cut = RenderList();
        FocusedList(cut).KeyDown("ArrowDown");
        FocusedList(cut).KeyDown("ArrowDown");

        _messages = Recreated();

        cut.WaitForAssertion(() =>
            ActiveRowText(cut).Should().ContainSingle().Which.Should().Contain("second"));
        ActiveRowText(cut).Should().ContainSingle();
    }

    [Test]
    public void Enter_ThenLiveContentReplace_KeepsPreviousAndNextAvailable()
    {
        var cut = RenderList();
        FocusedList(cut).KeyDown("ArrowDown");
        FocusedList(cut).KeyDown("ArrowDown");
        FocusedList(cut).KeyDown("Enter");
        cut.Find("button[title='Previous message']").HasAttribute("disabled").Should().BeFalse();
        cut.Find("button[title='Next message']").HasAttribute("disabled").Should().BeFalse();

        _messages = Recreated();

        cut.WaitForAssertion(() =>
        {
            cut.Find("button[title='Previous message']").HasAttribute("disabled").Should().BeFalse();
            cut.Find("button[title='Next message']").HasAttribute("disabled").Should().BeFalse();
        });
        cut.Markup.Should().Contain("Message Details");
    }

    [Test]
    public void ArrowRight_InMessageDetail_OpensTheNextMessage()
    {
        var cut = RenderList();
        FocusedList(cut).KeyDown("ArrowDown");
        FocusedList(cut).KeyDown("Enter");

        cut.Find(".payload-detail-toolbar").KeyDown("ArrowRight");

        cut.Find(".payload-detail-body").TextContent.Should().Contain("second");
    }

    [Test]
    public void ArrowLeft_InMessageDetail_OpensThePreviousMessage()
    {
        var cut = RenderList();
        FocusedList(cut).KeyDown("ArrowDown");
        FocusedList(cut).KeyDown("ArrowDown");
        FocusedList(cut).KeyDown("Enter");

        cut.Find(".payload-detail-toolbar").KeyDown("ArrowLeft");

        cut.Find(".payload-detail-body").TextContent.Should().Contain("first");
    }

    [Test]
    public void ArrowKeys_InTheJsonTree_ToggleNodesWithoutChangingTheSelectedMessage()
    {
        _messages = Payloads("""{"a":{"b":{"c":1}}}""", "third");
        var cut = RenderList(2);
        cut.FindAll(".payload-row")[0].Click();

        cut.WaitForAssertion(() => cut.FindAll(".json-toggle").Should().NotBeEmpty());
        cut.FindAll(".json-toggle").Last().KeyDown("ArrowRight");
        cut.FindAll(".json-toggle").Last().GetAttribute("aria-expanded").Should().Be("true");

        cut.FindAll(".json-toggle").Last().KeyDown("ArrowLeft");
        cut.FindAll(".json-toggle").Last().GetAttribute("aria-expanded").Should().Be("false");

        cut.Find(".payload-detail-body").TextContent.Should().Contain("\"c\"");

        cut.Find(".payload-detail-toolbar").KeyDown("ArrowRight");
        cut.Find(".payload-detail-body").TextContent.Should().Contain("third");
    }

    [Test]
    public void Escape_InMessageDetail_ReturnsToTheMessageList()
    {
        var cut = RenderList();
        FocusedList(cut).KeyDown("ArrowDown");
        FocusedList(cut).KeyDown("Enter");

        cut.Find(".payload-detail-toolbar").KeyDown("Escape");

        cut.FindAll(".payload-message-list").Should().ContainSingle();
        cut.Markup.Should().NotContain("Message Details");
    }

    [Test]
    public void Enter_MovesFocusIntoTheMessageDetail()
    {
        var cut = RenderList();

        FocusedList(cut).KeyDown("ArrowDown");
        FocusedList(cut).KeyDown("Enter");

        JSInterop.VerifyFocusAsyncInvoke(1, "opening a message moves focus once");
        LastFocusTarget().ShouldBeElementReferenceTo(cut.Find(".payload-detail-toolbar"));
    }

    [Test]
    public void BackToTheList_MovesFocusBackToTheMessageList()
    {
        var cut = RenderList();
        FocusedList(cut).KeyDown("ArrowDown");
        FocusedList(cut).KeyDown("Enter");

        cut.Find("button[title='Back to message list']").Click();

        JSInterop.VerifyFocusAsyncInvoke(2, "returning to the list moves focus back");
        LastFocusTarget().ShouldBeElementReferenceTo(cut.Find(".payload-message-list"));
    }

    [Test]
    public void LiveContentUpdate_DoesNotMoveFocus()
    {
        var cut = RenderList();
        FocusedList(cut).KeyDown("ArrowDown");
        FocusedList(cut).KeyDown("Enter");

        _messages = Recreated();

        cut.WaitForAssertion(() => _metrics.Received().SetDisplayedMessageCount(3));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Message Details"));
        JSInterop.VerifyFocusAsyncInvoke(1, "refreshing content must leave focus where it is");
    }

    [Test]
    public void ScopedClearOfTheOpenMessage_ClosesTheDetailAndReturnsFocusToTheList()
    {
        var selection = new TopicBulkSelection(TopicTreeSelectionTestAdapter.Create());
        var cut = Render<PayloadBrowser>(parameters => parameters.AddCascadingValue(selection));
        cut.WaitForAssertion(() => cut.FindAll(".payload-row").Should().HaveCount(3));

        FocusedList(cut).KeyDown("ArrowDown");
        FocusedList(cut).KeyDown("Enter");
        cut.Markup.Should().Contain("Message Details");

        cut.InvokeAsync(() => selection.NotifyMessagesCleared(
            [new TopicBranchTarget(TopicBranchKind.Raw, Path: "sensor/temp")]));

        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("Message Details"));
        cut.FindAll(".payload-message-list").Should().ContainSingle();
        LastFocusTarget().ShouldBeElementReferenceTo(cut.Find(".payload-message-list"));
    }

    private object LastFocusTarget() =>
        JSInterop.Invocations.Last(invocation => invocation.Identifier == "Blazor._internal.domWrapper.focus").Arguments[0]!;

    private List<MqttMessage> Recreated()
    {
        _selection = _selection with { Token = new SelectedTopicToken(_storeId, 1, 2) };
        return _messages
            .Select(message => new MqttMessage
            {
                Id = message.Id,
                Topic = message.Topic,
                Payload = message.Payload,
                DateTimeReceived = message.DateTimeReceived
            })
            .ToList();
    }
}
