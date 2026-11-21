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

        Services.AddSingleton(Substitute.For<IUxMetricsService>());
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

    private static async Task<IElement> FocusedListAsync(IRenderedComponent<PayloadBrowser> cut)
    {
        var list = cut.Find(".payload-message-list");
        await list.FocusAsync(new FocusEventArgs());
        return list;
    }

    private static Task PressListKeyAsync(IRenderedComponent<PayloadBrowser> cut, string key) =>
        PressListKeyAsync(cut, new KeyboardEventArgs { Key = key });

    private static async Task PressListKeyAsync(IRenderedComponent<PayloadBrowser> cut, KeyboardEventArgs args)
    {
        var list = await FocusedListAsync(cut);
        await list.KeyDownAsync(args);
    }

    private const string KeyboardModulePath = "./_content/MqttProbe.UI/js/message-list-keyboard.js";
    private const string FocusIdentifier = "Blazor._internal.domWrapper.focus";

    private Task<JSRuntimeInvocation> ExpectFocusAsync()
    {
        var invoked = new TaskCompletionSource<JSRuntimeInvocation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = JSInterop.SetupVoid(FocusIdentifier, invocation =>
        {
            invoked.TrySetResult(invocation);
            return true;
        });
        handler.SetVoidResult();
        return invoked.Task;
    }

    private static IReadOnlyList<string> ActiveRowText(IRenderedComponent<PayloadBrowser> cut) =>
        cut.FindAll(".payload-row--active").Select(row => row.TextContent).ToList();

    [Test]
    public async Task ClickingAMessageRow_StillOpensTheMessageDetail()
    {
        var cut = RenderList();

        await cut.FindAll(".payload-row")[1].ClickAsync();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Message Details");
            cut.Find(".payload-detail-body").TextContent.Should().Contain("second");
        });
    }

    [Test]
    public async Task FilteredList_WalksOnlyMatchingMessages()
    {
        var cut = RenderList();
        await List(cut).QuerySelector("input")!.InputAsync("third");
        cut.WaitForAssertion(() => cut.FindAll(".payload-row").Should().ContainSingle());

        await PressListKeyAsync(cut, "End");
        ActiveRowText(cut).Should().ContainSingle().Which.Should().Contain("third");

        await PressListKeyAsync(cut, "Enter");
        cut.WaitForAssertion(() => cut.Find(".payload-detail-body").TextContent.Should().Contain("third"));
    }

    [Test]
    public async Task ActiveMessage_IsExposedToAssistiveTechnology()
    {
        var cut = RenderList();

        await PressListKeyAsync(cut, "ArrowDown");

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
    public async Task TimeSort_IsANativeButtonThatReflectsTheCurrentOrder()
    {
        var cut = RenderList();

        var sort = cut.Find("button.sortable-th");
        sort.GetAttribute("type").Should().Be("button");
        sort.GetAttribute("aria-label").Should().Be("Sort messages by time");
        sort.GetAttribute("title").Should().Be("Sort: oldest first");
        cut.Find("thead th").GetAttribute("aria-sort").Should().Be("descending");
        cut.FindAll(".payload-row")[0].TextContent.Should().Contain("first");

        await sort.ClickAsync();

        cut.WaitForAssertion(() => cut.FindAll(".payload-row")[0].TextContent.Should().Contain("third"));
        cut.Find("button.sortable-th").GetAttribute("title").Should().Be("Sort: newest first");
        cut.Find("thead th").GetAttribute("aria-sort").Should().Be("ascending");
    }

    [Test]
    public async Task End_WhileTheNewTopicIsStillPending_DoesNotActivateTheOldTopicMessage()
    {
        _selection = Selection("root", generation: 1);
        _messages = Payloads("root-only");
        var cut = RenderList(1);

        _selection = Selection("/long80", generation: 2);
        _messages = Payloads("long-1", "long-2", "long-3");

        await PressListKeyAsync(cut, "End");

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
    public async Task ArrowKeys_OnlyNavigateWhileTheListItselfHasFocus()
    {
        var cut = RenderList();
        var list = List(cut);

        await list.FocusAsync(new FocusEventArgs());
        await list.KeyDownAsync(new KeyboardEventArgs { Key = "ArrowDown" });
        ActiveRowText(cut).Should().ContainSingle().Which.Should().Contain("first");

        // Focus moves into a nested control, such as the message search field.
        list = List(cut);
        await list.BlurAsync(new FocusEventArgs());
        await list.KeyDownAsync(new KeyboardEventArgs { Key = "ArrowDown" });

        ActiveRowText(cut).Should().ContainSingle().Which.Should().Contain("first");
    }

    [Test]
    public async Task ArrowDown_MarksTheFirstMessageAsActive()
    {
        var cut = RenderList();

        await PressListKeyAsync(cut, "ArrowDown");

        ActiveRowText(cut).Should().ContainSingle().Which.Should().Contain("first");
    }

    [Test]
    public async Task ArrowDown_ThenArrowDown_MovesToTheSecondMessage()
    {
        var cut = RenderList();

        await PressListKeyAsync(cut, "ArrowDown");
        await PressListKeyAsync(cut, "ArrowDown");

        ActiveRowText(cut).Should().ContainSingle().Which.Should().Contain("second");
    }

    [Test]
    public async Task ArrowUp_AtTheFirstMessage_StaysOnTheFirstMessage()
    {
        var cut = RenderList();

        await PressListKeyAsync(cut, "ArrowDown");
        await PressListKeyAsync(cut, "ArrowUp");

        ActiveRowText(cut).Should().ContainSingle().Which.Should().Contain("first");
    }

    [Test]
    public async Task End_ThenHome_MoveBetweenTheLastAndFirstMessage()
    {
        var cut = RenderList();

        await PressListKeyAsync(cut, "End");
        ActiveRowText(cut).Should().ContainSingle().Which.Should().Contain("third");

        await PressListKeyAsync(cut, "Home");
        ActiveRowText(cut).Should().ContainSingle().Which.Should().Contain("first");
    }

    [Test]
    public async Task ArrowKeys_DuringImeComposition_DoNotMoveTheCursor()
    {
        var cut = RenderList();
        var list = await FocusedListAsync(cut);

        await list.KeyDownAsync(new KeyboardEventArgs { Key = "ArrowDown", IsComposing = true });

        ActiveRowText(cut).Should().BeEmpty();
    }

    [TestCase("Enter")]
    [TestCase(" ")]
    public async Task AutoRepeatedOpenKey_DoesNotOpenTheActiveMessage(string key)
    {
        var cut = RenderList();
        var list = await FocusedListAsync(cut);
        await list.KeyDownAsync(new KeyboardEventArgs { Key = "ArrowDown" });

        list = List(cut);
        await list.KeyDownAsync(new KeyboardEventArgs { Key = key, Repeat = true });

        cut.Markup.Should().NotContain("Message Details");
    }

    [Test]
    public async Task Enter_OpensTheActiveMessage()
    {
        var cut = RenderList();

        await PressListKeyAsync(cut, "ArrowDown");
        await PressListKeyAsync(cut, "ArrowDown");
        cut.WaitForAssertion(() => ActiveRowText(cut).Should().ContainSingle()
            .Which.Should().Contain("second"));
        await PressListKeyAsync(cut, "Enter");

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Message Details");
            cut.Find(".payload-detail-body").TextContent.Should().Contain("second");
        });
    }

    [Test]
    public async Task Space_OpensTheActiveMessage()
    {
        var cut = RenderList();

        await PressListKeyAsync(cut, "End");
        cut.WaitForAssertion(() => ActiveRowText(cut).Should().ContainSingle()
            .Which.Should().Contain("third"));
        await PressListKeyAsync(cut, " ");

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Message Details");
            cut.Find(".payload-detail-body").TextContent.Should().Contain("third");
        });
    }

    [TestCase("Control")]
    [TestCase("Alt")]
    [TestCase("Meta")]
    public async Task ModifiedArrow_DoesNotMoveTheActiveMessage(string modifier)
    {
        var cut = RenderList();
        var args = new KeyboardEventArgs
        {
            Key = "ArrowDown",
            CtrlKey = modifier == "Control",
            AltKey = modifier == "Alt",
            MetaKey = modifier == "Meta"
        };

        await PressListKeyAsync(cut, args);

        ActiveRowText(cut).Should().BeEmpty();
        cut.Markup.Should().NotContain("Message Details");
    }

    [Test]
    public async Task ArrowKeyFromTheSearchField_DoesNotMoveTheActiveMessage()
    {
        var cut = RenderList();

        await List(cut).QuerySelector("input")!.KeyDownAsync(new KeyboardEventArgs { Key = "ArrowDown" });

        ActiveRowText(cut).Should().BeEmpty();
    }

    [Test]
    public async Task ArrowDown_WhenNoMessages_DoesNothing()
    {
        _messages = [];
        var cut = Render<PayloadBrowser>();
        cut.WaitForAssertion(() => cut.FindAll(".payload-row").Should().BeEmpty());

        var act = () => PressListKeyAsync(cut, "ArrowDown");

        await act.Should().NotThrowAsync();
        cut.Markup.Should().NotContain("Message Details");
    }

    [Test]
    public async Task ActiveMessage_KeepsItsRow_WhenLiveContentReplacesTheMessages()
    {
        var cut = RenderList();
        await PressListKeyAsync(cut, "ArrowDown");
        await PressListKeyAsync(cut, "ArrowDown");

        _messages = Recreated();

        cut.WaitForAssertion(() =>
            ActiveRowText(cut).Should().ContainSingle().Which.Should().Contain("second"));
        ActiveRowText(cut).Should().ContainSingle();
    }

    [Test]
    public async Task Enter_ThenLiveContentReplace_KeepsPreviousAndNextAvailable()
    {
        var cut = RenderList();
        await PressListKeyAsync(cut, "ArrowDown");
        await PressListKeyAsync(cut, "ArrowDown");
        await PressListKeyAsync(cut, "Enter");
        cut.WaitForElement(".payload-detail-toolbar");
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
    public async Task ArrowRight_InMessageDetail_OpensTheNextMessage()
    {
        var cut = RenderList();
        await PressListKeyAsync(cut, "ArrowDown");
        await PressListKeyAsync(cut, "Enter");

        var toolbar = cut.WaitForElement(".payload-detail-toolbar");
        toolbar.KeyDown("ArrowRight");

        cut.WaitForAssertion(() => cut.Find(".payload-detail-body").TextContent.Should().Contain("second"));
    }

    [Test]
    public async Task ArrowLeft_InMessageDetail_OpensThePreviousMessage()
    {
        var cut = RenderList();
        await PressListKeyAsync(cut, "ArrowDown");
        await PressListKeyAsync(cut, "ArrowDown");
        await PressListKeyAsync(cut, "Enter");

        var toolbar = cut.WaitForElement(".payload-detail-toolbar");
        toolbar.KeyDown("ArrowLeft");

        cut.WaitForAssertion(() => cut.Find(".payload-detail-body").TextContent.Should().Contain("first"));
    }

    [Test]
    public async Task ArrowKeys_InTheJsonTree_ToggleNodesWithoutChangingTheSelectedMessage()
    {
        _messages = Payloads("""{"a":{"b":{"c":1}}}""", "third");
        var cut = RenderList(2);
        await cut.FindAll(".payload-row")[0].ClickAsync();

        cut.WaitForAssertion(() => cut.FindAll(".json-toggle").Should().NotBeEmpty());
        var toggle = cut.FindAll(".json-toggle").Last();
        await toggle.KeyDownAsync(new KeyboardEventArgs { Key = "ArrowRight" });
        cut.FindAll(".json-toggle").Last().GetAttribute("aria-expanded").Should().Be("true");

        toggle = cut.FindAll(".json-toggle").Last();
        await toggle.KeyDownAsync(new KeyboardEventArgs { Key = "ArrowLeft" });
        cut.FindAll(".json-toggle").Last().GetAttribute("aria-expanded").Should().Be("false");

        cut.Find(".payload-detail-body").TextContent.Should().Contain("\"c\"");

        var toolbar = cut.WaitForElement(".payload-detail-toolbar");
        toolbar.KeyDown("ArrowRight");
        cut.WaitForAssertion(() => cut.Find(".payload-detail-body").TextContent.Should().Contain("third"));
    }

    [Test]
    public async Task Escape_InMessageDetail_ReturnsToTheMessageList()
    {
        var cut = RenderList();
        await List(cut).FocusAsync(new FocusEventArgs());
        await List(cut).KeyDownAsync(new KeyboardEventArgs { Key = "ArrowDown" });
        cut.WaitForAssertion(() => ActiveRowText(cut).Should().ContainSingle()
            .Which.Should().Contain("first"));

        await List(cut).KeyDownAsync(new KeyboardEventArgs { Key = "Enter" });
        var toolbar = cut.WaitForElement(".payload-detail-toolbar");
        await toolbar.KeyDownAsync(new KeyboardEventArgs { Key = "Escape" });

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".payload-message-list").Should().ContainSingle();
            cut.Markup.Should().NotContain("Message Details");
        });
    }

    [Test]
    public async Task Enter_MovesFocusIntoTheMessageDetail()
    {
        var keyboard = JSInterop.SetupModule(KeyboardModulePath);
        keyboard.Setup<int?>("attach", _ => true).SetResult(1);
        var detachReached = new TaskCompletionSource<JSRuntimeInvocation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var detach = keyboard.SetupVoid("detach", invocation =>
        {
            detachReached.TrySetResult(invocation);
            return true;
        });
        var focusReached = ExpectFocusAsync();

        var cut = RenderList();
        await PressListKeyAsync(cut, "ArrowDown");

        var enter = List(cut).KeyDownAsync(new KeyboardEventArgs { Key = "Enter" });
        await detachReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cut.WaitForAssertion(() => cut.FindAll(".payload-detail-toolbar").Should().ContainSingle());
        JSInterop.VerifyNotInvoke(FocusIdentifier, "focus must not be requested before the list detaches");

        detach.SetVoidResult();
        var focus = await focusReached.WaitAsync(TimeSpan.FromSeconds(5));
        await enter;

        JSInterop.VerifyFocusAsyncInvoke(1, "opening a message moves focus once");
        focus.Arguments[0].ShouldBeElementReferenceTo(cut.Find(".payload-detail-toolbar"));
    }

    [Test]
    public async Task BackToTheList_MovesFocusBackToTheMessageList()
    {
        var keyboard = JSInterop.SetupModule(KeyboardModulePath);
        keyboard.Setup<int?>("attach", _ => true).SetResult(1);
        keyboard.SetupVoid("detach", _ => true).SetVoidResult();
        var focusOpened = ExpectFocusAsync();

        var cut = RenderList();
        await PressListKeyAsync(cut, "ArrowDown");
        await PressListKeyAsync(cut, "Enter");
        var openFocus = await focusOpened.WaitAsync(TimeSpan.FromSeconds(5));
        openFocus.Arguments[0].ShouldBeElementReferenceTo(cut.Find(".payload-detail-toolbar"));

        var attachReached = new TaskCompletionSource<JSRuntimeInvocation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var attach = keyboard.Setup<int?>("attach", invocation =>
        {
            attachReached.TrySetResult(invocation);
            return true;
        });
        var focusReturned = ExpectFocusAsync();
        var back = cut.Find("button[title='Back to message list']").ClickAsync();
        await attachReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cut.WaitForAssertion(() => cut.FindAll(".payload-message-list").Should().ContainSingle());
        JSInterop.VerifyFocusAsyncInvoke(1, "the list cannot take focus until it is attached");

        attach.SetResult(1);
        var returnFocus = await focusReturned.WaitAsync(TimeSpan.FromSeconds(5));
        await back;

        JSInterop.VerifyFocusAsyncInvoke(2, "returning to the list moves focus back");
        returnFocus.Arguments[0].ShouldBeElementReferenceTo(cut.Find(".payload-message-list"));
    }

    [Test]
    public async Task LiveContentUpdate_DoesNotMoveFocus()
    {
        var cut = RenderList();
        await PressListKeyAsync(cut, "ArrowDown");
        await PressListKeyAsync(cut, "Enter");
        cut.WaitForElement(".payload-detail-toolbar");

        _messages = Recreated("first-refreshed");

        cut.WaitForAssertion(() => cut.Find(".payload-detail-body").TextContent.Should().Contain("first-refreshed"));
        JSInterop.VerifyFocusAsyncInvoke(1, "refreshing content must leave focus where it is");
    }

    [Test]
    public async Task ScopedClearOfTheOpenMessage_ClosesTheDetailAndReturnsFocusToTheList()
    {
        var selection = new TopicBulkSelection(TopicTreeSelectionTestAdapter.Create());
        var cut = Render<PayloadBrowser>(parameters => parameters.AddCascadingValue(selection));
        cut.WaitForAssertion(() => cut.FindAll(".payload-row").Should().HaveCount(3));

        await PressListKeyAsync(cut, "ArrowDown");
        await PressListKeyAsync(cut, "Enter");
        cut.WaitForElement(".payload-detail-toolbar");

        var focusReturned = ExpectFocusAsync();
        await cut.InvokeAsync(() => selection.NotifyMessagesCleared(
            [new TopicBranchTarget(TopicBranchKind.Raw, Path: "sensor/temp")]));

        var focus = await focusReturned.WaitAsync(TimeSpan.FromSeconds(5));
        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("Message Details"));
        cut.FindAll(".payload-message-list").Should().ContainSingle();
        JSInterop.VerifyFocusAsyncInvoke(2, "closing the detail returns focus to the list");
        focus.Arguments[0].ShouldBeElementReferenceTo(cut.Find(".payload-message-list"));
    }

    private List<MqttMessage> Recreated(string? firstPayload = null)
    {
        _selection = _selection with { Token = new SelectedTopicToken(_storeId, 1, 2) };
        return _messages
            .Select(message => new MqttMessage
            {
                Id = message.Id,
                Topic = message.Topic,
                Payload = message.Payload == "first" && firstPayload is not null
                    ? firstPayload
                    : message.Payload,
                DateTimeReceived = message.DateTimeReceived
            })
            .ToList();
    }
}
