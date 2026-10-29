using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.UI.Tests.TestHelpers;

namespace MqttProbe.UI.Tests.Components.Browser;

// Keyboard support is additive: pointer and touch still select and expand as before, and the
// rendered tree stays the only seam these tests reach through.
[TestFixture]
public class TopicBrowserKeyboardNavigationTests : BunitTestContext
{
    private const string ModulePath = "./_content/MqttProbe.UI/js/topic-tree-keyboard.js";

    private IMessageStoreManager _mockMsgStore = null!;
    private TopicBrowserTestFixture _fixture = null!;

    [SetUp]
    public void SetupMocks()
    {
        _mockMsgStore = Substitute.For<IMessageStoreManager>();
        _fixture = new TopicBrowserTestFixture(_mockMsgStore);
        Services.AddSingleton<ITopicSelectionStore>(_fixture.Adapter);
        Services.AddUiSettings(SettingsSubstituteRegistration.CreateUiSettings());
    }

    private IRenderedComponent<TopicBrowser> RenderExpandedTree() => RenderTopics(expandAll: true);

    private IRenderedComponent<TopicBrowser> RenderTopics(
        bool expandAll = false,
        Func<SelectedTopicState, Task>? onSelected = null)
    {
        _fixture.AddTopics(TopicBrowserTestTrees.BuildTopics());
        var cut = Render<TopicBrowser>(parameters =>
        {
            if (onSelected is not null)
                parameters.Add(component => component.OnSelectedChanged, onSelected);
        });
        EnsureMudProviders();
        if (expandAll)
        {
            cut.FindAll(".mud-toolbar button")[0].Click();
            cut.WaitForAssertion(() => cut.FindAll(".topic-tree-row").Should().HaveCount(4));
        }

        return cut;
    }

    [Test]
    public void RenderedTree_UsesTreeAndTreeItemRolesWithDepthAndExpansion()
    {
        var cut = RenderExpandedTree();

        var tree = cut.Find("[role='tree']");
        tree.GetAttribute("aria-label").Should().Be("Topics");

        var rows = cut.FindAll("[role='treeitem']");
        rows.Should().HaveCount(4);
        rows[0].GetAttribute("aria-level").Should().Be("1");
        rows[1].GetAttribute("aria-level").Should().Be("2");
        rows[0].GetAttribute("aria-expanded").Should().Be("true");
        rows[0].GetAttribute("aria-selected").Should().Be("false");
        rows[1].HasAttribute("aria-expanded").Should().BeFalse(
            "a leaf has no expansion state to report");
    }

    [Test]
    public void RenderedTree_KeepsOneTabStopOnTheFirstRow()
    {
        var cut = RenderExpandedTree();

        var tabStops = cut.FindAll(".topic-tree-row[tabindex='0']");
        tabStops.Should().ContainSingle();
        tabStops[0].GetAttribute("data-topic-key").Should().Be("sensors");
        cut.FindAll(".topic-tree-row[tabindex='-1']").Should().HaveCount(3);
    }

    [Test]
    public async Task ArrowKeys_MoveTheTabStopInVisibleOrderWithoutSelecting()
    {
        var cut = RenderExpandedTree();

        await PressKeyAsync(cut, "ArrowDown");
        TabStopKey(cut).Should().Be("sensors/humidity");

        await PressKeyAsync(cut, "ArrowDown");
        TabStopKey(cut).Should().Be("sensors/temp");

        await PressKeyAsync(cut, "ArrowUp");
        TabStopKey(cut).Should().Be("sensors/humidity");

        _mockMsgStore.DidNotReceive().SelectTopics(Arg.Any<TopicSelection?>());
    }

    [Test]
    public async Task HomeAndEnd_MoveTheTabStopToTheFirstAndLastVisibleRow()
    {
        var cut = RenderExpandedTree();

        await PressKeyAsync(cut, "End");
        TabStopKey(cut).Should().Be("sensors/temp/room1");

        await PressKeyAsync(cut, "Home");
        TabStopKey(cut).Should().Be("sensors");
    }

    [Test]
    public async Task RightArrow_ExpandsACollapsedParentThenMovesToItsFirstChild()
    {
        var cut = RenderTopics();
        cut.FindAll(".topic-tree-row").Should().HaveCount(1);

        await PressKeyAsync(cut, "ArrowRight");
        cut.WaitForAssertion(() => cut.FindAll(".topic-tree-row").Should().HaveCount(3));
        cut.Find(".topic-tree-row[data-topic-key='sensors']").GetAttribute("aria-expanded").Should().Be("true");
        TabStopKey(cut).Should().Be("sensors");

        await PressKeyAsync(cut, "ArrowRight");
        TabStopKey(cut).Should().Be("sensors/humidity");
    }

    [Test]
    public async Task LeftArrow_MovesToTheParentThenCollapsesIt()
    {
        var cut = RenderExpandedTree();
        await PressKeyAsync(cut, "End");
        TabStopKey(cut).Should().Be("sensors/temp/room1");

        await PressKeyAsync(cut, "ArrowLeft");
        TabStopKey(cut).Should().Be("sensors/temp");

        await PressKeyAsync(cut, "ArrowLeft");
        cut.WaitForAssertion(() => cut.FindAll(".topic-tree-row").Should().HaveCount(3));
        TabStopKey(cut).Should().Be("sensors/temp");
        cut.Find(".topic-tree-row[data-topic-key='sensors/temp']").GetAttribute("aria-expanded")
            .Should().Be("false");
    }

    [TestCase("Enter")]
    [TestCase(" ")]
    public async Task ActivationKeys_SelectTheFocusedRowThroughTheExistingSelectionPath(string key)
    {
        SelectedTopicState? published = null;
        var cut = RenderTopics(expandAll: true, onSelected: state =>
        {
            published = state;
            return Task.CompletedTask;
        });

        await PressKeyAsync(cut, "End");
        await PressKeyAsync(cut, key);

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".topic-tree-row--selected").Should().ContainSingle()
                .Which.GetAttribute("data-topic-key").Should().Be("sensors/temp/room1");
            cut.Find(".topic-tree-row[data-topic-key='sensors/temp/room1']").GetAttribute("aria-selected")
                .Should().Be("true");
            TabStopKey(cut).Should().Be("sensors/temp/room1");
        });

        var selected = published.Should().BeOfType<SelectedTopicState>().Which;
        selected.Key.Should().Be("sensors/temp/room1");
        _mockMsgStore.Received().SelectTopics(
            Arg.Is<TopicSelection>(selection => selection.Key == "sensors/temp/room1"));
    }

    [Test]
    public async Task ModifiedKeys_AreLeftToTheirStandardShortcuts()
    {
        var cut = RenderExpandedTree();

        foreach (var args in new[]
                 {
                     new KeyboardEventArgs { Key = "ArrowDown", CtrlKey = true },
                     new KeyboardEventArgs { Key = "End", AltKey = true },
                     new KeyboardEventArgs { Key = "ArrowUp", MetaKey = true },
                     new KeyboardEventArgs { Key = "ArrowDown", ShiftKey = true },
                     new KeyboardEventArgs { Key = "Enter", CtrlKey = true }
                 })
        {
            await PressKeyAsync(cut, args);
        }

        TabStopKey(cut).Should().Be("sensors");
        _mockMsgStore.DidNotReceive().SelectTopics(Arg.Any<TopicSelection?>());
    }

    [Test]
    public async Task ComposingKeystrokes_DoNotNavigate()
    {
        var cut = RenderExpandedTree();

        await PressKeyAsync(cut, new KeyboardEventArgs { Key = "ArrowDown", IsComposing = true });

        TabStopKey(cut).Should().Be("sensors");
    }

    [Test]
    public async Task RepeatedActivation_DoesNotSelectAgain()
    {
        var cut = RenderExpandedTree();

        await PressKeyAsync(cut, new KeyboardEventArgs { Key = "Enter", Repeat = true });

        _mockMsgStore.DidNotReceive().SelectTopics(Arg.Any<TopicSelection?>());
    }

    [Test]
    public async Task Refresh_FallsBackToTheNearestVisibleAncestor_WhenTheTabStopRowDisappears()
    {
        var cut = RenderExpandedTree();
        await PressKeyAsync(cut, "End");
        TabStopKey(cut).Should().Be("sensors/temp/room1");

        _fixture.Adapter.Store.RemoveMatchingTopic("sensors/temp/room1");
        await cut.InvokeAsync(() => cut.Instance.RefreshAsync());

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".topic-tree-row").Should().HaveCount(3);
            TabStopKey(cut).Should().Be("sensors/temp");
        });
    }

    [Test]
    public void ClickingARow_MovesTheTabStopToItAndSelectsIt()
    {
        var cut = RenderExpandedTree();

        cut.Find(".topic-tree-row[data-topic-key='sensors/temp']").Click();

        cut.WaitForAssertion(() =>
        {
            TabStopKey(cut).Should().Be("sensors/temp");
            cut.Find(".topic-tree-row[data-topic-key='sensors/temp']").GetAttribute("aria-selected")
                .Should().Be("true");
        });
    }

    [Test]
    public async Task LiveUpdate_KeepsFocusRequestOnTheSameRow_WhenItsPositionMoves()
    {
        var module = JSInterop.SetupModule(ModulePath);
        module.SetupVoid("retainFocus");
        var cut = RenderExpandedTree();

        await MoveAndFocusAsync(cut, "ArrowDown");
        TabStopKey(cut).Should().Be("sensors/humidity");

        _fixture.AddTopics(("sensors/air", 1));
        await cut.InvokeAsync(() => cut.Instance.RefreshAsync());

        cut.WaitForAssertion(() => TabStopKey(cut).Should().Be("sensors/humidity"));
        var retain = module.Invocations.Last(invocation => invocation.Identifier == "retainFocus");
        retain.Arguments[1].Should().Be("sensors/humidity");
        retain.Arguments[2].Should().Be(2);
    }

    [Test]
    public async Task KeyboardMove_AsksThePageToFocusTheNewRow()
    {
        var module = JSInterop.SetupModule(ModulePath);
        module.SetupVoid("moveFocus");
        var cut = RenderExpandedTree();

        await PressKeyAsync(cut, "ArrowDown");

        module.VerifyInvoke("moveFocus");
    }

    [Test]
    public async Task UnmountReport_AsksThePageToRestoreTheFocusedRow()
    {
        var module = JSInterop.SetupModule(ModulePath);
        module.SetupVoid("restoreFocus");
        var cut = RenderExpandedTree();
        await MoveAndFocusAsync(cut, "ArrowDown");

        await cut.InvokeAsync(() => cut.Instance.OnFocusedRowUnmountedAsync());

        var restore = module.Invocations.Should().ContainSingle(invocation => invocation.Identifier == "restoreFocus")
            .Subject;
        restore.Arguments[1].Should().Be("sensors/humidity");
        restore.Arguments[2].Should().Be(1);
    }

    [Test]
    public async Task Refresh_KeepsTheTabStopOnTheSameRow_WhenItsPositionMoves()
    {
        var cut = RenderExpandedTree();
        await PressKeyAsync(cut, "ArrowDown");
        TabStopKey(cut).Should().Be("sensors/humidity");

        _fixture.AddTopics(("sensors/air", 1));
        await cut.InvokeAsync(() => cut.Instance.RefreshAsync());

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".topic-tree-row").Should().HaveCount(5);
            cut.FindAll(".topic-tree-row")[1].GetAttribute("data-topic-key").Should().Be("sensors/air");
            TabStopKey(cut).Should().Be("sensors/humidity");
        });
    }

    [Test]
    public async Task Refresh_FallsBackToTheFirstRow_WhenNoAncestorSurvives()
    {
        var cut = RenderExpandedTree();
        await PressKeyAsync(cut, "End");

        _fixture.Adapter.Store.RemoveMatchingTopic("sensors/#");
        _fixture.AddTopics(("other", 1));
        await cut.InvokeAsync(() => cut.Instance.RefreshAsync());

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".topic-tree-row").Should().ContainSingle();
            TabStopKey(cut).Should().Be("other");
        });
    }

    [Test]
    public async Task Refresh_AsksThePageToFocusTheAncestor_WhenTheFocusedRowDisappears()
    {
        var module = JSInterop.SetupModule(ModulePath);
        module.SetupVoid("restoreFocus");
        var cut = RenderExpandedTree();
        cut.Find(".topic-tree-row[data-topic-key='sensors/temp/room1']").Click();

        _fixture.Adapter.Store.RemoveMatchingTopic("sensors/temp/room1");
        await cut.InvokeAsync(() => cut.Instance.RefreshAsync());

        cut.WaitForAssertion(() => TabStopKey(cut).Should().Be("sensors/temp"));
        var focusCall = module.Invocations.Should().ContainSingle(invocation => invocation.Identifier == "restoreFocus")
            .Subject;
        focusCall.Arguments[1].Should().Be("sensors/temp");
    }

    [Test]
    public async Task Refresh_DoesNotTakeFocus_WhenTheTreeWasNeverUsed()
    {
        var module = JSInterop.SetupModule(ModulePath);
        module.SetupVoid("restoreFocus");
        var cut = RenderExpandedTree();

        _fixture.Adapter.Store.RemoveMatchingTopic("sensors/#");
        _fixture.AddTopics(("other", 1));
        await cut.InvokeAsync(() => cut.Instance.RefreshAsync());

        cut.WaitForAssertion(() => TabStopKey(cut).Should().Be("other"));
        module.Invocations.Where(invocation => invocation.Identifier is "restoreFocus" or "retainFocus" or "moveFocus")
            .Should().BeEmpty();
    }

    [Test]
    public async Task TypingInTheFilter_DoesNotReachTheTree()
    {
        var cut = RenderExpandedTree();
        await PressKeyAsync(cut, "ArrowDown");

        var filter = cut.Find(".topic-filter-field input");
        filter.KeyDown("ArrowDown");
        filter.KeyDown("End");

        TabStopKey(cut).Should().Be("sensors/humidity");
    }

    [Test]
    public async Task TwoMountedBrowsers_KeepIndependentTabStops()
    {
        var first = RenderTopics(expandAll: true);
        var second = Render<TopicBrowser>();
        second.FindAll(".mud-toolbar button")[0].Click();
        second.WaitForAssertion(() => second.FindAll(".topic-tree-row").Should().HaveCount(4));

        await PressKeyAsync(first, "ArrowDown");

        TabStopKey(first).Should().Be("sensors/humidity");
        TabStopKey(second).Should().Be("sensors");
    }

    [Test]
    public void Chevron_StaysPointerOperableButOutOfTheTabOrder()
    {
        var cut = RenderExpandedTree();
        var chevron = cut.Find(".topic-tree-row[data-topic-key='sensors'] button");

        chevron.GetAttribute("tabindex").Should().Be("-1");
        chevron.Click();

        cut.WaitForAssertion(() => cut.FindAll(".topic-tree-row").Should().ContainSingle());
        TabStopKey(cut).Should().Be("sensors");
    }

    private static Task PressKeyAsync(IRenderedComponent<TopicBrowser> cut, KeyboardEventArgs args) =>
        cut.InvokeAsync(() => cut.Find(".topic-tree-row[tabindex='0']").KeyDown(args));

    private static Task PressKeyAsync(IRenderedComponent<TopicBrowser> cut, string key) =>
        cut.InvokeAsync(() => cut.Find(".topic-tree-row[tabindex='0']").KeyDown(key));

    // The page focuses the row a completed move lands on; bUnit has to do that itself.
    private static async Task MoveAndFocusAsync(IRenderedComponent<TopicBrowser> cut, string key)
    {
        await PressKeyAsync(cut, key);
        cut.Find(".topic-tree-row[tabindex='0']").Focus();
    }

    private static string? TabStopKey(IRenderedComponent<TopicBrowser> cut)
    {
        var tabStops = cut.FindAll(".topic-tree-row[tabindex='0']");
        tabStops.Should().ContainSingle("a tree keeps exactly one roving tab stop");
        return tabStops[0].GetAttribute("data-topic-key");
    }
}

