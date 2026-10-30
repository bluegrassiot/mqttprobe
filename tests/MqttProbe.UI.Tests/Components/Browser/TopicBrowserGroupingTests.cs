using AngleSharp.Dom;
using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.UI.Tests.TestHelpers;

namespace MqttProbe.UI.Tests.Components.Browser;

[TestFixture]
public class TopicBrowserGroupingTests : BunitTestContext
{
    private const string NodeTip = "spBv1.0/plant1/NDATA/node1";
    private const string DeviceTip = "spBv1.0/plant1/DDATA/node1/dev1";

    private IMessageStoreManager _mockMsgStore = null!;
    private TopicBrowserTestFixture _fixture = null!;
    private IUiSettings _mockUi = null!;
    private readonly UiPreferences _preferences = new();

    [SetUp]
    public void SetupMocks()
    {
        _mockMsgStore = Substitute.For<IMessageStoreManager>();
        _fixture = new TopicBrowserTestFixture(_mockMsgStore);
        Services.AddSingleton<ITopicSelectionStore>(_fixture.Adapter);
        _mockUi = SettingsSubstituteRegistration.CreateUiSettings(_preferences);
        Services.AddSingleton(_mockMsgStore);
        Services.AddUiSettings(_mockUi);
    }

    private static (string Topic, int Messages)[] SparkplugTree() =>
    [
        (NodeTip, 3),
        ("spBv1.0/plant1/NBIRTH/node1", 1),
        (DeviceTip, 2),
        ("spBv1.0/STATE/scada", 1),
        ("sensors/temp", 4)
    ];

    private IRenderedComponent<TopicBrowser> RenderGrouped(
        bool grouped,
        bool expandAll = true,
        Func<SelectedTopicState, Task>? onSelected = null)
    {
        _preferences.GroupSparkplugTopicsByNodeAndDevice = grouped;
        _fixture.AddTopics(SparkplugTree());
        var cut = Render<TopicBrowser>(parameters => parameters
            .Add(component => component.OnSelectedChanged, state => onSelected?.Invoke(state) ?? Task.CompletedTask));
        EnsureMudProviders();
        if (expandAll)
            ExpandAll(cut);

        return cut;
    }

    private static void ExpandAll(IRenderedComponent<TopicBrowser> cut) =>
        cut.FindAll(".mud-toolbar button")[0].Click();

    private static void CollapseAll(IRenderedComponent<TopicBrowser> cut) =>
        cut.FindAll(".mud-toolbar button")[1].Click();

    private static IElement RowWith(IRenderedComponent<TopicBrowser> cut, string text) =>
        cut.FindAll(".topic-tree-row").First(row => row.TextContent.Contains(text, StringComparison.Ordinal));

    private void ApplyPreference(bool grouped)
    {
        _preferences.GroupSparkplugTopicsByNodeAndDevice = grouped;
        _mockUi.UiPreferencesChanged += Raise.Event<Action>();
    }

    [Test]
    public void GroupingOn_ShowsGroupedTreeAndFlagsTheActivePresentation()
    {
        var cut = RenderGrouped(grouped: true);

        cut.Markup.Should().Contain("Grouped by node and device");
        cut.FindAll(".topic-tree-row--group").Should().NotBeEmpty();
        cut.Markup.Should().Contain("plant1");
        cut.Markup.Should().Contain("node1");
        cut.Markup.Should().Contain("dev1");
        cut.Markup.Should().Contain("NBIRTH");
        cut.Markup.Should().Contain(NodeTip);
        cut.Markup.Should().Contain(DeviceTip);
    }

    [Test]
    public void GroupingOn_KeepsUnsupportedTopicsInTheLiteralHierarchy()
    {
        var cut = RenderGrouped(grouped: true);

        // STATE is a real Sparkplug topic, not a grouping verb, so it stays literal.
        cut.Markup.Should().Contain("scada");
        cut.Markup.Should().Contain("STATE");
        cut.Markup.Should().Contain("sensors");
    }

    [Test]
    public void GroupingOff_KeepsTheLiteralTreeAndHidesTheFlag()
    {
        var cut = RenderGrouped(grouped: false);

        cut.Markup.Should().NotContain("Grouped by node and device");
        cut.FindAll(".topic-tree-row--group").Should().BeEmpty();
        cut.Markup.Should().NotContain("Devices");
        cut.Markup.Should().Contain("NDATA");
        cut.Markup.Should().Contain("DDATA");
    }

    [Test]
    public async Task GroupingRows_AreMarkedAndOnlyTheSelectedRowIsHighlighted()
    {
        var cut = RenderGrouped(grouped: true);
        RowWith(cut, "node1").Click();
        await cut.InvokeAsync(() => cut.Instance.RefreshAsync());

        var groupingRows = cut.FindAll(".topic-tree-row--group");
        var selectedRows = cut.FindAll(".topic-tree-row--selected");

        selectedRows.Should().ContainSingle()
            .Which.ClassList.Should().Contain("topic-tree-row--group");
        groupingRows.Should().Contain(row => row.ClassList.Contains("topic-tree-row--selected"));
    }

    [Test]
    public void ClickingRealTopicRow_SelectsThatTopicAndPublishesIt()
    {
        SelectedTopicState? callbackState = null;
        var cut = RenderGrouped(grouped: true, onSelected: state =>
        {
            callbackState = state;
            return Task.CompletedTask;
        });

        RowWith(cut, NodeTip).Click();

        _mockMsgStore.Received(1).SelectTopics(Arg.Is<TopicSelection>(selection =>
            selection.Key == NodeTip && selection.Topics.SequenceEqual(new[] { NodeTip })));
        callbackState.Should().NotBeNull();
        callbackState!.Key.Should().Be(NodeTip);
    }

    [Test]
    public void ClickingNodeRow_SelectsEveryTopicUnderTheNodeWithoutTogglingExpansion()
    {
        SelectedTopicState? callbackState = null;
        var cut = RenderGrouped(grouped: true, onSelected: state =>
        {
            callbackState = state;
            return Task.CompletedTask;
        });
        cut.Markup.Should().Contain("NDATA");

        RowWith(cut, "node1").Click();

        _fixture.Adapter.Selections.Should().ContainSingle();
        var request = _fixture.Adapter.Selections[0]!;
        request.Mode.Should().Be(TopicSelectionMode.Branches);
        request.Branches.Should().ContainSingle().Which.Scope.Should().Be(
            new SparkplugSelectionScope(SparkplugSelectionKind.Node, "spBv1.0/plant1", "node1"));
        callbackState.Should().NotBeNull();
        callbackState!.MessageCount.Should().Be(6,
            "the node scope covers every verb, device and future member");
        cut.Markup.Should().Contain("NDATA", "the click selected rather than collapsed");
    }

    [Test]
    public void ClickingDeviceRow_SelectsThatDevicesTopics()
    {
        var cut = RenderGrouped(grouped: true);

        RowWith(cut, "dev1").Click();

        _fixture.Adapter.Selections.Should().ContainSingle();
        _fixture.Adapter.Selections[0]!.Branches.Should().ContainSingle().Which.Scope.Should().Be(
            new SparkplugSelectionScope(SparkplugSelectionKind.Device, "spBv1.0/plant1", "node1", "dev1"));
    }

    [Test]
    public void ClickingMessageTypeRow_SelectsTheTopicUnderIt()
    {
        var cut = RenderGrouped(grouped: true);

        RowWith(cut, "NBIRTH").Click();

        _fixture.Adapter.Selections.Should().ContainSingle();
        var branch = _fixture.Adapter.Selections[0]!.Branches.Should().ContainSingle().Which;
        branch.Scope.Should().Be(
            new SparkplugSelectionScope(SparkplugSelectionKind.Node, "spBv1.0/plant1", "node1"));
        branch.MessageType.Should().Be("NBIRTH");
    }

    [Test]
    public void TurningGroupingOn_RebuildsTheTreeImmediately()
    {
        var cut = RenderGrouped(grouped: false);
        cut.Markup.Should().NotContain("Grouped by node and device");
        cut.Markup.Should().Contain("NDATA");

        ApplyPreference(true);

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Grouped by node and device");
            cut.Markup.Should().Contain("node1");
            cut.Markup.Should().NotContain("NDATA");
        });
    }

    [Test]
    public void TurningGroupingOff_RebuildsTheTreeAndDropsGroupingExpansion()
    {
        var cut = RenderGrouped(grouped: true);
        cut.FindAll(".topic-tree-row--group").Should().NotBeEmpty();

        ApplyPreference(false);

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".topic-tree-row--group").Should().BeEmpty();
            cut.Markup.Should().Contain("NDATA");
        });

        ApplyPreference(true);

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("node1");
            cut.FindAll(".topic-tree-row--group").Should().NotBeEmpty();
            cut.Markup.Should().NotContain(NodeTip);
        });
    }

    [Test]
    public void ARawVerbBranch_KeepsItsMessages_AndChecksTheGroupedTypeRowItCovers()
    {
        var cut = RenderGrouped(grouped: false);
        cut.Find(".topic-select-toggle").Click();
        cut.WaitForAssertion(() => cut.FindAll(".topic-select-checkbox").Should().NotBeEmpty());

        RowByPath(cut, "spBv1.0/plant1/NDATA").QuerySelector(".topic-select-checkbox input")!.Change(true);

        var selected = _fixture.Adapter.Store.GetSelectedTopicState();
        selected.Branches.Should().ContainSingle().Which.Path.Should().Be("spBv1.0/plant1/NDATA");
        selected.MessageCount.Should().Be(3);

        ApplyPreference(true);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Grouped by node and device"));
        ExpandAll(cut);
        var after = _fixture.Adapter.Store.GetSelectedTopicState();
        after.Branches.Should().ContainSingle().Which.Path.Should().Be("spBv1.0/plant1/NDATA",
            "changing the presentation does not drop the selection");
        _fixture.Adapter.Store.GetSelectedMessages(after.Token, 10)!.Messages.Should().HaveCount(3,
            "the messages stay selected");

        RowByLabel(cut, "NDATA").QuerySelector(".topic-select-checkbox input[checked]").Should().NotBeNull(
            "every topic the NDATA row names is under the selected verb row");
        RowByLabel(cut, "node1").QuerySelector(".topic-select-checkbox .mud-checkbox-null").Should().NotBeNull(
            "the node row also names other verbs, so it is only partly selected");
        RowByLabel(cut, "NBIRTH").QuerySelector(".topic-select-checkbox input[checked]").Should().BeNull();
    }

    private static IElement RowByPath(IRenderedComponent<TopicBrowser> cut, string path) =>
        cut.FindAll(".topic-tree-row").First(row => row.GetAttribute("data-topic-path") == path);

    private static IElement RowByLabel(IRenderedComponent<TopicBrowser> cut, string label) =>
        cut.FindAll(".topic-tree-row")
            .First(row => row.QuerySelector(".topic-name")?.TextContent.Trim() == label);

    [Test]
    public void UnrelatedPreferenceChange_DoesNotRegroupTheTree()
    {
        var cut = RenderGrouped(grouped: false);
        var before = cut.Markup;

        _preferences.Theme = "light";
        _mockUi.UiPreferencesChanged += Raise.Event<Action>();

        cut.Markup.Should().Be(before);
    }

    [Test]
    public void CollapseAll_AfterGroupedExpandAll_LeavesOnlyRootRows()
    {
        var cut = RenderGrouped(grouped: true);
        cut.FindAll(".topic-tree-row").Count.Should().BeGreaterThan(2);

        CollapseAll(cut);

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".topic-tree-row").Select(row => row.TextContent)
                .Should().OnlyContain(text => text.Contains("spBv1.0") || text.Contains("sensors"));
            cut.Markup.Should().NotContain("NDATA");
        });
    }

    [Test]
    public void Filter_RetainsGroupedChainForMatchingDevice()
    {
        var cut = RenderGrouped(grouped: true, expandAll: false);

        cut.Find(".topic-filter-field input").Input("dev1");

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("dev1");
            cut.Markup.Should().Contain(DeviceTip);
            cut.Markup.Should().NotContain("scada");
            cut.Markup.Should().NotContain("sensors");
        });
    }

    [Test]
    public void Filter_MatchesTheDisplayedTopicPathOfAGroupedLeaf()
    {
        var cut = RenderGrouped(grouped: true, expandAll: false);

        cut.Find(".topic-filter-field input").Input(NodeTip);

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("NDATA");
            cut.Markup.Should().Contain(NodeTip);
            cut.Markup.Should().NotContain("Devices");
            cut.Markup.Should().NotContain("scada");
        });
    }

    [Test]
    public void Filter_MatchesAMultiSegmentPathFragment()
    {
        var cut = RenderGrouped(grouped: true, expandAll: false);

        cut.Find(".topic-filter-field input").Input("plant1/NDATA");

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("NDATA");
            cut.Markup.Should().Contain(NodeTip);
            cut.Markup.Should().NotContain(DeviceTip);
        });
    }

    [Test]
    public async Task DisposeAsync_RemovesThePreferenceHandler()
    {
        var cut = RenderGrouped(grouped: true);

        await cut.Instance.DisposeAsync();

        _mockUi.Received().UiPreferencesChanged -= Arg.Any<Action>();
    }
}
