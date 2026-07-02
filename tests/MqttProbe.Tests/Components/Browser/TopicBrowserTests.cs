using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Models.Mqtt;
using MqttProbe.Services.Mqtt;
using MqttProbe.Shared.Tests.TestHelpers;

namespace MqttProbe.Shared.Tests.Components.Browser;

[TestFixture]
public class TopicBrowserTests : BunitTestContext
{
    private IMessageStoreManager _mockMsgStore = null!;

    [SetUp]
    public void SetupMocks()
    {
        _mockMsgStore = Substitute.For<IMessageStoreManager>();
        _mockMsgStore.MessageStores.Returns(
            new ConcurrentDictionary<string, MessageStore>());
        Services.AddSingleton(_mockMsgStore);
    }

    [Test]
    public void Renders_NoMessagesText_WhenMessageStoresIsEmpty()
    {
        var cut = Render<TopicBrowser>();

        cut.Markup.Should().Contain("No messages received");
    }

    [Test]
    public void Renders_TreeItems_WhenMessageStoresHasEntries()
    {
        var stores = new ConcurrentDictionary<string, MessageStore>();
        stores["sensor"] = new MessageStore
        {
            Topic = "sensor",
            Messages = new ConcurrentQueue<MqttMessage>()
        };
        _mockMsgStore.MessageStores.Returns(stores);

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
        var stores = new ConcurrentDictionary<string, MessageStore>();
        stores["sensor"] = new MessageStore
        {
            Topic = "sensor",
            Messages = new ConcurrentQueue<MqttMessage>()
        };
        _mockMsgStore.MessageStores.Returns(stores);

        var cut = Render<TopicBrowser>();
        await Task.Delay(50); // let the immediate timer tick dispatch a render

        var act = async () => await cut.Instance.DisposeAsync();

        await act.Should().NotThrowAsync();
    }

    [Test]
    public void DoesNotTriggerStateHasChanged_WhenVersionUnchanged()
    {
        var stores = new ConcurrentDictionary<string, MessageStore>();
        stores["sensor"] = new MessageStore
        {
            Topic = "sensor",
            Messages = new ConcurrentQueue<MqttMessage>()
        };
        _mockMsgStore.MessageStores.Returns(stores);
        _mockMsgStore.GetVersion().Returns(100L);

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
        _mockMsgStore.GetVersion().Returns(100L, 101L);

        var cut = Render<TopicBrowser>();
        EnsureMudProviders();
        cut.Markup.Should().Contain("No messages received");

        // Add a store so the re-render produces different markup.
        var stores = new ConcurrentDictionary<string, MessageStore>();
        stores["sensor"] = new MessageStore
        {
            Topic = "sensor",
            Messages = new ConcurrentQueue<MqttMessage>()
        };
        _mockMsgStore.MessageStores.Returns(stores);

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

    [SetUp]
    public void SetupMocks()
    {
        _mockMsgStore = Substitute.For<IMessageStoreManager>();
        _mockMsgStore.MessageStores.Returns(new ConcurrentDictionary<string, MessageStore>());
        _mockMsgStore.MaxTopicNodes.Returns(10000);
        _mockMsgStore.GetVersion().Returns(0L);
        Services.AddSingleton(_mockMsgStore);
    }

    internal static MessageStore BuildTree()
    {
        var sensors = new MessageStore { Topic = "sensors", FullTopic = "sensors" };
        var temp = new MessageStore { Topic = "temp", FullTopic = "sensors/temp", Parent = sensors };
        var room1 = new MessageStore { Topic = "room1", FullTopic = "sensors/temp/room1", Parent = temp };
        var humidity = new MessageStore { Topic = "humidity", FullTopic = "sensors/humidity", Parent = sensors };

        // room1: 2 messages
        room1.Messages = new ConcurrentQueue<MqttMessage>();
        room1.Messages.Enqueue(new MqttMessage("r1-1", "sensors/temp/room1", false, MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce));
        room1.Messages.Enqueue(new MqttMessage("r1-2", "sensors/temp/room1", false, MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce));

        // temp: 1 message (aggregate = 1 + 2 from room1 = 3)
        temp.Messages = new ConcurrentQueue<MqttMessage>();
        temp.Messages.Enqueue(new MqttMessage("t-1", "sensors/temp", false, MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce));

        // humidity: 2 messages
        humidity.Messages = new ConcurrentQueue<MqttMessage>();
        humidity.Messages.Enqueue(new MqttMessage("h-1", "sensors/humidity", false, MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce));
        humidity.Messages.Enqueue(new MqttMessage("h-2", "sensors/humidity", false, MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce));

        sensors.Messages = new ConcurrentQueue<MqttMessage>();

        temp.SubTopics = new ConcurrentDictionary<string, MessageStore> { ["room1"] = room1 };
        room1.SubTopics = new ConcurrentDictionary<string, MessageStore>();
        sensors.SubTopics = new ConcurrentDictionary<string, MessageStore> { ["temp"] = temp, ["humidity"] = humidity };
        humidity.SubTopics = new ConcurrentDictionary<string, MessageStore>();

        // Set maintained aggregate counts (as MessageStoreManager would maintain them).
        // TopicCount: total descendant topics in subtree.
        // MessageCount: total messages in subtree (own + all descendants).
        room1.TopicCount = 0;
        room1.MessageCount = 2;
        temp.TopicCount = 1;      // room1
        temp.MessageCount = 3;    // 1 own + 2 from room1
        humidity.TopicCount = 0;
        humidity.MessageCount = 2;
        sensors.TopicCount = 3;   // temp + room1 + humidity
        sensors.MessageCount = 5; // 3 from temp subtree + 2 from humidity

        return sensors;
    }

    [Test]
    public void RendersRootNodes_WhenDataExists()
    {
        var stores = new ConcurrentDictionary<string, MessageStore> { ["sensors"] = BuildTree() };
        _mockMsgStore.MessageStores.Returns(stores);

        var cut = Render<TopicBrowser>();
        EnsureMudProviders();

        cut.Markup.Should().Contain("sensors");
    }

    [Test]
    public void CollapsedRoot_DoesNotRenderChildren()
    {
        var stores = new ConcurrentDictionary<string, MessageStore> { ["sensors"] = BuildTree() };
        _mockMsgStore.MessageStores.Returns(stores);

        var cut = Render<TopicBrowser>();
        EnsureMudProviders();

        cut.Markup.Should().NotContain("temp");
        cut.Markup.Should().NotContain("humidity");
    }

    [Test]
    public void ShowCounts_ForRootNode()
    {
        var stores = new ConcurrentDictionary<string, MessageStore> { ["sensors"] = BuildTree() };
        _mockMsgStore.MessageStores.Returns(stores);

        var cut = Render<TopicBrowser>();
        EnsureMudProviders();

        cut.Markup.Should().Contain("5");
    }
}

[TestFixture]
public class TopicBrowserInteractionTests : BunitTestContext
{
    private IMessageStoreManager _mockMsgStore = null!;

    [SetUp]
    public void SetupMocks()
    {
        _mockMsgStore = Substitute.For<IMessageStoreManager>();
        _mockMsgStore.MessageStores.Returns(new ConcurrentDictionary<string, MessageStore>());
        _mockMsgStore.MaxTopicNodes.Returns(10000);
        _mockMsgStore.GetVersion().Returns(0L);
        Services.AddSingleton(_mockMsgStore);
    }

    private IRenderedComponent<TopicBrowser> RenderWithTree()
    {
        var stores = new ConcurrentDictionary<string, MessageStore>
        {
            ["sensors"] = TopicBrowserVirtualizedTests.BuildTree()
        };
        _mockMsgStore.MessageStores.Returns(stores);
        var cut = Render<TopicBrowser>();
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
    public void ClickingRow_SetsSelectedMessageStore()
    {
        var cut = RenderWithTree();

        cut.Find(".topic-tree-row").Click();

        _mockMsgStore.Received().SelectedMessageStore =
            Arg.Is<MessageStore>(s => s.FullTopic == "sensors");
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
        _mockMsgStore.Received().SelectedMessageStore = null;
    }

    [Test]
    public void Selection_StaleCssCleared_WhenManagerSelectionBecomesNull()
    {
        var cut = RenderWithTree();

        // Select a row
        cut.Find(".topic-tree-row").Click();
        cut.Find(".topic-tree-row").ClassName.Should().Contain("topic-tree-row--selected");

        // Simulate external clear (e.g., ClearAllMessages)
        _mockMsgStore.SelectedMessageStore = null;

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
        var stores = new ConcurrentDictionary<string, MessageStore>
        {
            ["sensors"] = TopicBrowserVirtualizedTests.BuildTree()
        };
        _mockMsgStore.MessageStores.Returns(stores);
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
        var stores = new ConcurrentDictionary<string, MessageStore>
        {
            ["sensors"] = TopicBrowserVirtualizedTests.BuildTree()
        };
        _mockMsgStore.MessageStores.Returns(stores);
        var cut = Render<TopicBrowser>();
        EnsureMudProviders();

        // sensors is structural-only (empty Messages queue) with children → Folder icon.
        // LucideIcons.Folder SVG path contains the unique 'd' attribute for a folder shape.
        var rootRow = cut.Find(".topic-tree-row");
        rootRow.InnerHtml.Should().Contain("M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z");
    }
}
