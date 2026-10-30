using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.UI.Components.Layout;
using MqttProbe.UI.Tests.TestHelpers;

namespace MqttProbe.UI.Tests.Components.Browser;

[TestFixture]
public class TopicBrowserTests : BunitTestContext
{
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

    [Test]
    public void Renders_NoMessagesText_WhenSnapshotIsEmpty()
    {
        var cut = Render<TopicBrowser>();

        cut.Markup.Should().Contain("No messages received");
    }

    [Test]
    public void Renders_TreeItems_WhenSnapshotHasRoots()
    {
        _fixture.AddTopics(("sensor", 1));

        var cut = Render<TopicBrowser>();

        cut.Markup.Should().NotContain("No messages received");
        cut.Markup.Should().Contain("sensor");
    }

    [Test]
    public async Task DisposeAsync_DoesNotThrow()
    {
        var cut = Render<TopicBrowser>();

        var act = async () => await cut.Instance.DisposeAsync();

        await act.Should().NotThrowAsync();
    }

    [Test]
    public async Task DisposeAsync_AfterTimerHasFired_DoesNotThrow()
    {
        _fixture.AddTopics(("sensor", 1));

        var cut = Render<TopicBrowser>();
        await cut.InvokeAsync(() => cut.Instance.RefreshAsync());

        var act = async () => await cut.Instance.DisposeAsync();

        await act.Should().NotThrowAsync();
    }

    [Test]
    public async Task DoesNotTriggerStateHasChanged_WhenVersionUnchanged()
    {
        _fixture.AddTopics(("sensor", 1));

        var cut = Render<TopicBrowser>();
        EnsureMudProviders();

        cut.Markup.Should().Contain("sensor");
        string markupBefore = cut.Markup;

        await cut.InvokeAsync(() => cut.Instance.RefreshAsync());

        cut.Markup.Should().Be(markupBefore);
    }

    [Test]
    public async Task TriggersStateHasChanged_WhenVersionIncrements()
    {
        var cut = Render<TopicBrowser>();
        EnsureMudProviders();
        cut.Markup.Should().Contain("No messages received");

        _fixture.AddTopics(("sensor", 1));
        await cut.InvokeAsync(() => cut.Instance.RefreshAsync());
        cut.Markup.Should().Contain("sensor");
    }
}

[TestFixture]
public class TopicBrowserVirtualizedTests : BunitTestContext
{
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

    internal static (string Topic, int Messages)[] BuildTopics() => TopicBrowserTestTrees.BuildTopics();

    [Test]
    public void RendersRootNodes_WhenDataExists()
    {
        _fixture.AddTopics(TopicBrowserTestTrees.BuildTopics());

        var cut = Render<TopicBrowser>();
        EnsureMudProviders();

        cut.Markup.Should().Contain("sensors");
    }

    [Test]
    public void CollapsedRoot_DoesNotRenderChildren()
    {
        _fixture.AddTopics(TopicBrowserTestTrees.BuildTopics());

        var cut = Render<TopicBrowser>();
        EnsureMudProviders();

        cut.Markup.Should().NotContain("temp");
        cut.Markup.Should().NotContain("humidity");
    }

    [Test]
    public void ShowCounts_ForRootNode()
    {
        _fixture.AddTopics(TopicBrowserTestTrees.BuildTopics());

        var cut = Render<TopicBrowser>();
        EnsureMudProviders();

        cut.Markup.Should().Contain("5");
    }
}

[TestFixture]
public class TopicBrowserInteractionTests : BunitTestContext
{
    private sealed record SnapshotReadGate(TaskCompletionSource<bool> Started, ManualResetEventSlim Release);

    private sealed class CoordinatedSelectionStore(ITopicSelectionStore inner) : ITopicSelectionStore
    {
        private SnapshotReadGate? _nextReadGate;

        public SnapshotReadGate? NextReadGate
        {
            set => Interlocked.Exchange(ref _nextReadGate, value);
        }

        public TopicTreeSnapshot GetTopicTreeSnapshot()
        {
            var gate = Interlocked.Exchange(ref _nextReadGate, null);
            if (gate is not null)
            {
                gate.Started.TrySetResult(true);
                if (!gate.Release.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("The coordinated snapshot read was not released.");
            }

            return inner.GetTopicTreeSnapshot();
        }

        public SelectedTopicState GetSelectedTopicState() => inner.GetSelectedTopicState();

        public void SelectTopics(TopicSelection? selection) => inner.SelectTopics(selection);

        public bool TryClearSelection(long treeVersion, SelectedTopicState expectedSelection) =>
            inner.TryClearSelection(treeVersion, expectedSelection);

        public Task<SelectedMessagesSnapshot?> GetSelectedMessagesAsync(SelectedTopicToken expectedSelection, int limit) =>
            inner.GetSelectedMessagesAsync(expectedSelection, limit);
    }

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

    private IRenderedComponent<TopicBrowser> RenderWithTree(Func<SelectedTopicState, Task>? onSelected = null)
    {
        _fixture.AddTopics(TopicBrowserTestTrees.BuildTopics());
        var cut = Render<TopicBrowser>(parameters =>
        {
            if (onSelected is not null)
                parameters.Add(component => component.OnSelectedChanged, onSelected);
        });
        EnsureMudProviders();
        return cut;
    }

    // The component refreshes on a timer, so a render can land between finding an element and
    // clicking it. Look-up and click share one dispatcher action to keep it on one frame.
    private static Task ClickAsync(IRenderedComponent<TopicBrowser> cut, string selector) =>
        cut.InvokeAsync(() => cut.Find(selector).Click());

    [Test]
    public void ClickingRow_AppliesSelectedClass()
    {
        var cut = RenderWithTree();

        var row = cut.Find(".topic-tree-row");
        row.ClassName.Should().NotContain("topic-tree-row--selected");

        row.Click();

        row.ClassName.Should().Contain("topic-tree-row--selected");
    }

    [Test]
    public void ClickingRow_RequestsManagerOwnedBranchSelection()
    {
        var cut = RenderWithTree();

        cut.Find(".topic-tree-row").Click();

        _mockMsgStore.Received().SelectTopics(Arg.Is<TopicSelection>(selection =>
            selection.Mode == TopicSelectionMode.Branches
            && selection.Branches.Length == 1
            && selection.Branches[0].Path == "sensors"));
    }

    [Test]
    public void ClickingRow_PublishesTheCheckedBranch()
    {
        SelectedTopicState? callbackState = null;
        var cut = RenderWithTree(state =>
        {
            callbackState = state;
            return Task.CompletedTask;
        });

        cut.Find(".topic-tree-row").Click();

        callbackState.Should().NotBeNull();
        callbackState!.Branches.Should().ContainSingle().Which.Path.Should().Be("sensors");
        callbackState.Token.Generation.Should().BeGreaterThan(0);
        _mockMsgStore.Received().SelectTopics(Arg.Is<TopicSelection>(selection =>
            selection.Mode == TopicSelectionMode.Branches));
    }

    [Test]
    public async Task ClickingAPurgedRow_LeavesNothingChecked()
    {
        SelectedTopicState? callbackState = null;
        var cut = RenderWithTree(state =>
        {
            callbackState = state;
            return Task.CompletedTask;
        });
        // The store loses the topic while the rendered row is still on screen; the click can only
        // check a branch nothing represents, so the next reconcile drops it again.
        _fixture.Adapter.Store.RemoveMatchingTopic("sensors/#");

        await ClickAsync(cut, ".topic-tree-row");

        cut.WaitForAssertion(() => cut.FindAll(".topic-tree-row--selected").Should().BeEmpty());
        _fixture.SelectedKey.Should().BeNull();
        _mockMsgStore.DidNotReceive().SelectTopic(Arg.Any<string?>());
        callbackState.Should().NotBeNull("the click still publishes the checked set it produced");
    }

    [Test]
    public async Task PurgedRow_IsRemovedByTheNextRefresh_AndPublishesNoSelection()
    {
        SelectedTopicState? callbackState = null;
        var cut = RenderWithTree(state =>
        {
            callbackState = state;
            return Task.CompletedTask;
        });

        _fixture.Clear();
        await cut.InvokeAsync(() => cut.Instance.RefreshAsync());
        cut.WaitForAssertion(() => cut.FindAll(".topic-tree-row").Should().BeEmpty());

        callbackState.Should().BeNull();
        _mockMsgStore.DidNotReceive().SelectTopics(Arg.Any<TopicSelection>());
    }

    [Test]
    public void ExpandRoot_ShowsChildrenInParentThenChildOrder()
    {
        var cut = RenderWithTree();

        var rows = cut.FindAll(".topic-tree-row");
        rows.Should().HaveCount(1);

        // Click the chevron button to expand sensors
        cut.Find(".topic-tree-row button").Click();

        cut.WaitForAssertion(() =>
        {
            var expandedRows = cut.FindAll(".topic-tree-row");
            expandedRows.Should().HaveCount(3);
            // Children sorted alphabetically: humidity before temp
            expandedRows[0].TextContent.Should().Contain("sensors");
            expandedRows[1].TextContent.Should().Contain("humidity");
            expandedRows[2].TextContent.Should().Contain("temp");
        });
    }

    [Test]
    public void ExpandedRoot_ShowsPerNodeAggregateCounts()
    {
        var cut = RenderWithTree();

        // Expand sensors
        cut.Find(".topic-tree-row button").Click();

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll(".topic-tree-row");
            rows.Should().HaveCount(3);

            // humidity: 0 subtopics + 2 messages (alphabetically first child)
            var humidityRow = rows[1];
            humidityRow.TextContent.Should().Contain("humidity");
            humidityRow.TextContent.Should().Contain("T 0");
            humidityRow.TextContent.Should().Contain("M 2");

            // temp: 1 subtopic (room1) + 3 messages (1 own + 2 from room1)
            var tempRow = rows[2];
            tempRow.TextContent.Should().Contain("temp");
            tempRow.TextContent.Should().Contain("T 1");
            tempRow.TextContent.Should().Contain("M 3");
        });
    }

    [Test]
    public void ShowsStatLegend_NearToolbar()
    {
        var cut = RenderWithTree();

        cut.Markup.Should().Contain("T = topics · M = messages");
    }

    [Test]
    public void ExpandAll_ThenCollapseAll_HidesAllChildren()
    {
        var cut = RenderWithTree();

        // Click ExpandAll toolbar button (first MudIconButton in the toolbar)
        var toolbarButtons = cut.FindAll(".mud-toolbar button");
        toolbarButtons[0].Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".topic-tree-row").Count.Should().BeGreaterThan(1);
        });

        // Click CollapseAll toolbar button (second MudIconButton)
        toolbarButtons = cut.FindAll(".mud-toolbar button");
        toolbarButtons[1].Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".topic-tree-row").Should().HaveCount(1);
            cut.Markup.Should().Contain("sensors");
            cut.Markup.Should().NotContain("temp");
        });
    }

    [Test]
    public async Task DirtySnapshotRefresh_IsSerializedWithCollapse_AndUnchangedPollKeepsLatestState()
    {
        _fixture.AddTopics(TopicBrowserTestTrees.BuildTopics());
        var coordinatedStore = new CoordinatedSelectionStore(_fixture.Adapter);
        Services.AddSingleton<ITopicSelectionStore>(coordinatedStore);
        var cut = Render<TopicBrowser>();
        EnsureMudProviders();
        await cut.InvokeAsync(() => cut.Find(".topic-tree-row button").Click());
        cut.FindAll(".topic-tree-row").Should().HaveCount(3);

        _fixture.Adapter.Add("sensors/humidity", new MqttMessage("new humidity", "unused", false,
            MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce)
        { Topic = "sensors/humidity" });
        using var releaseRead = new ManualResetEventSlim();
        var readStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinatedStore.NextReadGate = new SnapshotReadGate(readStarted, releaseRead);
        var dirtyRefresh = Task.Run(cut.Instance.RefreshAsync);
        await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var collapse = cut.InvokeAsync(() => cut.Find(".topic-tree-row button").Click());
        collapse.IsCompleted.Should().BeFalse("the refresh owns the renderer until its snapshot poll completes");
        releaseRead.Set();
        await dirtyRefresh.WaitAsync(TimeSpan.FromSeconds(5));
        await collapse.WaitAsync(TimeSpan.FromSeconds(5));
        cut.WaitForAssertion(() => cut.FindAll(".topic-tree-row").Should().ContainSingle());

        await cut.InvokeAsync(() => cut.Instance.RefreshAsync());
        cut.FindAll(".topic-tree-row").Should().ContainSingle();
        cut.Find(".topic-tree-row").TextContent.Should().Contain("sensors");
    }

    [Test]
    public void Filter_RevealsMatchingDescendants()
    {
        var cut = RenderWithTree();

        // Type a filter that matches only room1
        var filterInput = cut.Find(".topic-filter-field input");
        filterInput.Input("room1");

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll(".topic-tree-row");
            rows.Should().HaveCount(3); // sensors > temp > room1
            rows[0].TextContent.Should().Contain("sensors");
            rows[1].TextContent.Should().Contain("temp");
            rows[2].TextContent.Should().Contain("room1");
            cut.Markup.Should().NotContain("humidity");
        });
    }

    [Test]
    public void Filter_NoMatch_ShowsNoMatchMessage()
    {
        var cut = RenderWithTree();

        var filterInput = cut.Find(".topic-filter-field input");
        filterInput.Input("nonexistent");

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("No topics match");
            cut.FindAll(".topic-tree-row").Should().BeEmpty();
        });
    }

    [Test]
    public void Filter_KeepsTheCheckedSet_WhenChanged()
    {
        var cut = RenderWithTree();

        cut.Find(".topic-tree-row").Click();
        cut.Find(".topic-tree-row").ClassName.Should().Contain("topic-tree-row--selected");

        var filterInput = cut.Find(".topic-filter-field input");
        filterInput.Input("temp");

        cut.WaitForAssertion(() => cut.FindAll(".topic-tree-row").Should().HaveCount(2,
            "the match and its ancestor"));
        _fixture.Adapter.Store.GetSelectedTopicState().Branches.Should().ContainSingle()
            .Which.Path.Should().Be("sensors", "filtering is a view, not a selection change");
        cut.Find(".topic-tree-row--selected").GetAttribute("data-topic-path").Should().Be("sensors");

        filterInput.Input(string.Empty);
        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".topic-tree-row--selected").Should().ContainSingle()
                .Which.GetAttribute("data-topic-path").Should().Be("sensors");
        });
    }

    [Test]
    public async Task Selection_StaleCssCleared_WhenManagerSelectionBecomesNull()
    {
        var cut = RenderWithTree();

        await ClickAsync(cut, ".topic-tree-row");
        cut.Find(".topic-tree-row").ClassName.Should().Contain("topic-tree-row--selected");

        // Simulate an external clear of the store selection.
        _fixture.SetSelection(null);

        await ClickAsync(cut, ".topic-tree-row button");

        cut.WaitForAssertion(() =>
        {
            var selectedRows = cut.FindAll(".topic-tree-row--selected");
            selectedRows.Should().BeEmpty();
        });
    }

    [Test]
    public void ValueBearerLeaf_IsAGreenCircleWithoutASeparateMarker()
    {
        // humidity holds messages of its own while
        // sensors is structural only.
        _fixture.AddTopics(TopicBrowserTestTrees.BuildTopics());
        var cut = Render<TopicBrowser>();
        EnsureMudProviders();

        // Expand sensors to see children
        cut.Find(".topic-tree-row button").Click();

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll(".topic-tree-row");
            var humidityRow = rows.FirstOrDefault(r => r.TextContent.Contains("humidity", StringComparison.Ordinal));
            humidityRow.Should().NotBeNull();
            humidityRow!.QuerySelector(".topic-node-icon.topic-role--leaf").Should().NotBeNull(
                "an ordinary topic stays a green circle whether or not it holds messages");
            humidityRow.QuerySelector(".topic-direct-badge").Should().BeNull(
                "the separate message-presence marker is removed");
            humidityRow.InnerHtml.Should().NotContain(LucideIcons.Database);
        });
    }

    [Test]
    public void StructuralNode_RendersFolderIcon()
    {
        _fixture.AddTopics(TopicBrowserTestTrees.BuildTopics());
        var cut = Render<TopicBrowser>();
        EnsureMudProviders();

        // sensors is structural-only (empty Messages queue) with children → Folder icon.
        // LucideIcons.Folder SVG path contains the unique 'd' attribute for a folder shape.
        var rootRow = cut.Find(".topic-tree-row");
        rootRow.InnerHtml.Should().Contain("M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z");
    }
}
