using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Mqtt;
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
        _fixture.Configure();
        Services.AddSingleton(_mockMsgStore);
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
        _fixture.SetSnapshot(TopicBrowserTestTrees.Node("sensor"));

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
        _fixture.SetSnapshot(TopicBrowserTestTrees.Node("sensor"));

        var cut = Render<TopicBrowser>();
        await Task.Delay(50); // let the immediate timer tick dispatch a render

        var act = async () => await cut.Instance.DisposeAsync();

        await act.Should().NotThrowAsync();
    }

    [Test]
    public void DoesNotTriggerStateHasChanged_WhenVersionUnchanged()
    {
        _fixture.SetSnapshot(TopicBrowserTestTrees.Node("sensor"));

        var cut = Render<TopicBrowser>();
        EnsureMudProviders();

        // First timer tick (t=0) sees version 100 != _lastVersion 0 → counts computed but unchanged → no render.
        // Second tick should skip entirely because version hasn't changed.
        cut.Markup.Should().Contain("sensor");
        string markupBefore = cut.Markup;

        Thread.Sleep(1100);

        cut.Markup.Should().Be(markupBefore);
    }

    [Test]
    public void TriggersStateHasChanged_WhenVersionIncrements()
    {
        var cut = Render<TopicBrowser>();
        EnsureMudProviders();
        cut.Markup.Should().Contain("No messages received");

        // Add a store so the re-render produces different markup.
        _fixture.SetSnapshot(TopicBrowserTestTrees.Node("sensor"));

        // The timer at t=0 already fired with version 100L.
        // Advance once more: GetVersion() returns 101L → counts computed → re-render.
        Thread.Sleep(1100);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("sensor"));
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
        _fixture.Configure();
        Services.AddSingleton(_mockMsgStore);
    }

    internal static TopicNodeSnapshot BuildTree() => TopicBrowserTestTrees.BuildTree();

    [Test]
    public void RendersRootNodes_WhenDataExists()
    {
        _fixture.SetSnapshot(TopicBrowserTestTrees.BuildTree());

        var cut = Render<TopicBrowser>();
        EnsureMudProviders();

        cut.Markup.Should().Contain("sensors");
    }

    [Test]
    public void CollapsedRoot_DoesNotRenderChildren()
    {
        _fixture.SetSnapshot(TopicBrowserTestTrees.BuildTree());

        var cut = Render<TopicBrowser>();
        EnsureMudProviders();

        cut.Markup.Should().NotContain("temp");
        cut.Markup.Should().NotContain("humidity");
    }

    [Test]
    public void ShowCounts_ForRootNode()
    {
        _fixture.SetSnapshot(TopicBrowserTestTrees.BuildTree());

        var cut = Render<TopicBrowser>();
        EnsureMudProviders();

        cut.Markup.Should().Contain("5");
    }
}

[TestFixture]
public class TopicBrowserInteractionTests : BunitTestContext
{
    private IMessageStoreManager _mockMsgStore = null!;
    private TopicBrowserTestFixture _fixture = null!;

    private sealed record SnapshotReadGate(TaskCompletionSource<bool> Started, ManualResetEventSlim Release);

    [SetUp]
    public void SetupMocks()
    {
        _mockMsgStore = Substitute.For<IMessageStoreManager>();
        _fixture = new TopicBrowserTestFixture(_mockMsgStore);
        _fixture.Configure();
        Services.AddSingleton(_mockMsgStore);
    }

    private IRenderedComponent<TopicBrowser> RenderWithTree(Func<SelectedTopicState, Task>? onSelected = null)
    {
        _fixture.SetSnapshot(TopicBrowserTestTrees.BuildTree());
        var cut = Render<TopicBrowser>(parameters =>
        {
            if (onSelected is not null)
                parameters.Add(component => component.OnSelectedChanged, onSelected);
        });
        EnsureMudProviders();
        return cut;
    }

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
    public void ClickingRow_RequestsManagerOwnedSelection()
    {
        var cut = RenderWithTree();

        cut.Find(".topic-tree-row").Click();

        _mockMsgStore.Received().SelectTopic("sensors");
    }

    [Test]
    public void ClickingRow_PublishesManagerSelectionAfterSelectingPath()
    {
        SelectedTopicState? callbackState = null;
        var cut = RenderWithTree(state =>
        {
            callbackState = state;
            return Task.CompletedTask;
        });

        cut.Find(".topic-tree-row").Click();

        callbackState.Should().NotBeNull();
        callbackState!.FullTopic.Should().Be("sensors");
        callbackState.Token.Generation.Should().BeGreaterThan(0);
        _mockMsgStore.Received().SelectTopic("sensors");
    }

    [Test]
    public void ClickingPurgedRow_DoesNotPublishPreviousSelection()
    {
        SelectedTopicState? callbackState = null;
        var cut = RenderWithTree(state =>
        {
            callbackState = state;
            return Task.CompletedTask;
        });
        _fixture.SetSnapshot();

        cut.Find(".topic-tree-row").Click();

        callbackState.Should().BeNull();
        _mockMsgStore.Received().SelectTopic("sensors");
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
        var tree = TopicBrowserTestTrees.BuildTree();
        _fixture.SetSnapshot(tree);
        var snapshot = new TopicTreeSnapshot(20, System.Collections.Immutable.ImmutableArray.Create(tree));
        var nextReadStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        SnapshotReadGate? nextGate = null;
        using var releaseSnapshotRead = new ManualResetEventSlim();
        _mockMsgStore.GetTopicTreeSnapshot().Returns(_ =>
        {
            var gate = Interlocked.Exchange(ref nextGate, null);
            if (gate is not null)
            {
                gate.Started.TrySetResult(true);
                if (!gate.Release.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("The coordinated snapshot read was not released.");
            }

            Interlocked.Exchange(ref nextReadStarted, null)?.TrySetResult(true);
            return snapshot;
        });

        var cut = Render<TopicBrowser>();
        EnsureMudProviders();
        await cut.InvokeAsync(() => cut.Find(".topic-tree-row button").Click());
        cut.FindAll(".topic-tree-row").Should().HaveCount(3);

        snapshot = snapshot with { Version = 21 };
        var dirtyRefreshStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref nextGate, new SnapshotReadGate(dirtyRefreshStarted, releaseSnapshotRead));
        await dirtyRefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var collapse = cut.InvokeAsync(() => cut.Find(".topic-tree-row button").Click());
        try
        {
            collapse.IsCompleted.Should().BeFalse("the refresh owns the renderer until its snapshot poll completes");
        }
        finally
        {
            releaseSnapshotRead.Set();
        }

        await collapse.WaitAsync(TimeSpan.FromSeconds(5));
        cut.WaitForAssertion(() => cut.FindAll(".topic-tree-row").Should().ContainSingle());

        var unchangedPollStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref nextReadStarted, unchangedPollStarted);
        await unchangedPollStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cut.InvokeAsync(() => Task.CompletedTask);
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
    public void Filter_ClearsSelection_WhenChanged()
    {
        var cut = RenderWithTree();

        // First select a row
        cut.Find(".topic-tree-row").Click();
        cut.Find(".topic-tree-row").ClassName.Should().Contain("topic-tree-row--selected");

        // Type in the filter
        var filterInput = cut.Find(".topic-filter-field input");
        filterInput.Input("temp");

        cut.WaitForAssertion(() =>
        {
            // Visual selection should be cleared
            var selectedRows = cut.FindAll(".topic-tree-row--selected");
            selectedRows.Should().BeEmpty();
        });

        // Manager selection should also be cleared
        _mockMsgStore.Received().SelectTopic(null);
    }

    [Test]
    public void Selection_StaleCssCleared_WhenManagerSelectionBecomesNull()
    {
        var cut = RenderWithTree();

        // Select a row
        cut.Find(".topic-tree-row").Click();
        cut.Find(".topic-tree-row").ClassName.Should().Contain("topic-tree-row--selected");

        // Simulate external clear (e.g., ClearAllMessages)
        _fixture.SetSelection(new SelectedTopicState(new SelectedTopicToken(Guid.NewGuid(), 2, 0), null, 0));

        // Trigger a rebuild via expand — BuildVisibleRows syncs from manager
        cut.Find(".topic-tree-row button").Click();

        cut.WaitForAssertion(() =>
        {
            var selectedRows = cut.FindAll(".topic-tree-row--selected");
            selectedRows.Should().BeEmpty();
        });
    }

    [Test]
    public void ValueBearerLeaf_RendersDatabaseIcon()
    {
        // BuildTree() has humidity with 2 messages (value-bearing leaf).
        // sensors has empty Messages queue (structural-only).
        _fixture.SetSnapshot(TopicBrowserTestTrees.BuildTree());
        var cut = Render<TopicBrowser>();
        EnsureMudProviders();

        // Expand sensors to see children
        cut.Find(".topic-tree-row button").Click();

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll(".topic-tree-row");
            // Find the humidity row (has messages, value-bearing leaf)
            var humidityRow = rows.FirstOrDefault(r => r.TextContent.Contains("humidity"));
            humidityRow.Should().NotBeNull();
            // Value-bearing nodes should render with success color after Task 6.
            // MudBlazor MudIcon with Color.Success renders class "mud-success-text".
            humidityRow!.InnerHtml.Should().Contain("mud-success-text");
        });
    }

    [Test]
    public void StructuralNode_RendersFolderIcon()
    {
        _fixture.SetSnapshot(TopicBrowserTestTrees.BuildTree());
        var cut = Render<TopicBrowser>();
        EnsureMudProviders();

        // sensors is structural-only (empty Messages queue) with children → Folder icon.
        // LucideIcons.Folder SVG path contains the unique 'd' attribute for a folder shape.
        var rootRow = cut.Find(".topic-tree-row");
        rootRow.InnerHtml.Should().Contain("M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z");
    }
}
