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
        _fixture.Configure();
        _mockUi = SettingsSubstituteRegistration.CreateUiSettings(_preferences);
        Services.AddSingleton(_mockMsgStore);
        Services.AddUiSettings(_mockUi);
    }

    private static TopicNodeSnapshot[] SparkplugTree() =>
    [
        .. TopicSnapshotBuilder.Build(
            (NodeTip, 3),
            ("spBv1.0/plant1/NBIRTH/node1", 1),
            (DeviceTip, 2),
            ("spBv1.0/STATE/scada", 1),
            ("sensors/temp", 4))
    ];

    private IRenderedComponent<TopicBrowser> RenderGrouped(
        bool grouped,
        bool expandAll = true,
        Func<SelectedTopicState, Task>? onSelected = null)
    {
        _preferences.GroupSparkplugTopicsByNodeAndDevice = grouped;
        _fixture.SetSnapshot(SparkplugTree());
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
        cut.Markup.Should().Contain("Devices");
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
    public void GroupingRows_AreMarkedAndOnlyTheSelectedRowIsHighlighted()
    {
        _fixture.SetSelection(new SelectedTopicState(
            new SelectedTopicToken(Guid.NewGuid(), 1, 1), NodeTip, 3));
        var cut = RenderGrouped(grouped: true);

        var groupingRows = cut.FindAll(".topic-tree-row--group");
        var selectedRows = cut.FindAll(".topic-tree-row--selected");

        groupingRows.Should().NotBeEmpty();
        selectedRows.Should().ContainSingle()
            .Which.TextContent.Should().Contain(NodeTip);
        groupingRows.Should().NotContain(row => row.ClassList.Contains("topic-tree-row--selected"));
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

        _fixture.Selections.Should().ContainSingle();
        _fixture.Selections[0].Topics.Should().BeEquivalentTo(
        [
            NodeTip,
            "spBv1.0/plant1/NBIRTH/node1",
            DeviceTip
        ]);
        callbackState.Should().NotBeNull();
        cut.Markup.Should().Contain("NDATA", "the click selected rather than collapsed");
    }

    [Test]
    public void ClickingDevicesRow_SelectsOnlyDeviceTopics()
    {
        var cut = RenderGrouped(grouped: true);

        RowWith(cut, "Devices").Click();

        _fixture.Selections.Should().ContainSingle();
        _fixture.Selections[0].Topics.Should().BeEquivalentTo([DeviceTip]);
    }

    [Test]
    public void ClickingDeviceRow_SelectsThatDevicesTopics()
    {
        var cut = RenderGrouped(grouped: true);

        RowWith(cut, "dev1").Click();

        _fixture.Selections.Should().ContainSingle();
        _fixture.Selections[0].Topics.Should().BeEquivalentTo([DeviceTip]);
    }

    [Test]
    public void ClickingMessageTypeRow_SelectsTheTopicUnderIt()
    {
        var cut = RenderGrouped(grouped: true);

        RowWith(cut, "NBIRTH").Click();

        _fixture.Selections.Should().ContainSingle();
        _fixture.Selections[0].Topics.Should().BeEquivalentTo(["spBv1.0/plant1/NBIRTH/node1"]);
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
            cut.Markup.Should().Contain("Devices");
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
