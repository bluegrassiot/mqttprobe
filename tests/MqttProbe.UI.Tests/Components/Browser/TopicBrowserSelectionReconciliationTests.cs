using AngleSharp.Dom;
using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.UI.Tests.TestHelpers;

namespace MqttProbe.UI.Tests.Components.Browser;

// Changing how the tree is presented must never drop what is checked: the checked set is branch
// based, so it survives grouping, collapse and filtering, and Core is never asked to clear it.
[TestFixture]
public sealed class TopicBrowserSelectionReconciliationTests : BunitTestContext
{
    private const string NodeTip = "spBv1.0/plant1/NDATA/node1";
    private const string NodeVerb = "spBv1.0/plant1/NDATA";
    private const string GroupedMarker = "Grouped by node and device";

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

    private static (string Topic, int Messages)[] Topics() =>
    [
        (NodeTip, 3),
        ("spBv1.0/plant1/NDATA/node1/extra", 1),
        ("spBv1.0/plant1/NBIRTH/node1", 1),
        ("spBv1.0/plant1/DDATA/node1/dev1", 2),
        ("spBv1.0/plant1/STATE/scada", 1),
        ("spBv1.0/STATE/scada-host", 1),
        ("sensors/temp", 2)
    ];

    private IRenderedComponent<TopicBrowser> RenderWithBranch(bool grouped, string branchPath) =>
        RenderTopics(Topics(), grouped, branchPath);

    private IRenderedComponent<TopicBrowser> RenderTopics(
        IEnumerable<(string Topic, int Messages)> topics,
        bool grouped,
        string? branchPath)
    {
        _preferences.GroupSparkplugTopicsByNodeAndDevice = grouped;
        _fixture.AddTopics([.. topics]);
        if (branchPath is not null)
            SeedBranch(branchPath);
        var cut = Render<TopicBrowser>();
        EnsureMudProviders();
        return cut;
    }

    private void SeedBranch(string path) =>
        _fixture.Adapter.Store.SelectTopics(
            TopicSelection.ForBranches([new TopicBranchTarget(TopicBranchKind.Raw, Path: path)]));

    private static bool CheckedRow(IRenderedComponent<TopicBrowser> cut, string path) =>
        cut.FindAll(".topic-tree-row")
            .Single(row => string.Equals(row.GetAttribute("data-topic-path"), path, StringComparison.Ordinal))
            .GetAttribute("aria-selected") == "true";

    private string? SelectedBranchPath() =>
        _fixture.Adapter.Store.GetSelectedTopicState().Branches.Single().Path;

    private void ApplyPreference(bool grouped)
    {
        _preferences.GroupSparkplugTopicsByNodeAndDevice = grouped;
        _mockUi.UiPreferencesChanged += Raise.Event<Action>();
    }

    private static IElement RowFor(IRenderedComponent<TopicBrowser> cut, string label) =>
        cut.FindAll(".topic-tree-row").First(row => row.TextContent.Contains(label, StringComparison.Ordinal));

    private static void ExpandAll(IRenderedComponent<TopicBrowser> cut) =>
        cut.FindAll(".mud-toolbar button")[0].Click();

    private static void CollapseAll(IRenderedComponent<TopicBrowser> cut) =>
        cut.FindAll(".mud-toolbar button")[1].Click();

    private void UseRetentionStore()
    {
        _fixture = new TopicBrowserTestFixture(_mockMsgStore, maxStoredMessages: 0);
        Services.AddSingleton<ITopicSelectionStore>(_fixture.Adapter);
    }

    [Test]
    public void TurningGroupingOn_KeepsACheckedVerbBranchAndItsMessages()
    {
        var cut = RenderWithBranch(grouped: false, NodeVerb);
        ExpandAll(cut);
        RowFor(cut, "NDATA").ClassName.Should().Contain("topic-tree-row--selected");

        ApplyPreference(true);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain(GroupedMarker));
        ExpandAll(cut);
        cut.WaitForAssertion(() =>
        {
            RowFor(cut, "NDATA").ClassName.Should().Contain("topic-tree-row--selected");
            _fixture.Adapter.Store.GetSelectedTopicState().Branches.Should().ContainSingle()
                .Which.Path.Should().Be(NodeVerb);
            _fixture.Adapter.Store.GetSelectedMessages(
                    _fixture.Adapter.Store.GetSelectedTopicState().Token, 10)!.Messages
                .Should().HaveCount(4, "the branch still resolves its messages");
        });
        _mockMsgStore.DidNotReceive().SelectTopic(Arg.Any<string?>());
        _mockMsgStore.DidNotReceive().TryClearSelection(Arg.Any<long>(), Arg.Any<SelectedTopicState>());
    }

    [Test]
    public void TurningGroupingOn_KeepsACheckedGroupBranch()
    {
        var cut = RenderWithBranch(grouped: false, "spBv1.0/plant1");

        ApplyPreference(true);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain(GroupedMarker));
        ExpandAll(cut);
        cut.WaitForAssertion(() =>
        {
            RowFor(cut, "node1").ClassName.Should().Contain("topic-tree-row--selected");
            RowFor(cut, "dev1").ClassName.Should().Contain("topic-tree-row--selected");
        });
        _mockMsgStore.DidNotReceive().TryClearSelection(Arg.Any<long>(), Arg.Any<SelectedTopicState>());
    }

    [Test]
    public void TurningGroupingOff_KeepsACheckedTip()
    {
        var cut = RenderWithBranch(grouped: true, NodeTip);
        ExpandAll(cut);

        ApplyPreference(false);

        cut.WaitForAssertion(() => cut.Markup.Should().NotContain(GroupedMarker));
        ExpandAll(cut);
        cut.WaitForAssertion(() => RowFor(cut, NodeTip).ClassName.Should().Contain("topic-tree-row--selected"));
        _fixture.Adapter.Store.GetSelectedTopicState().Branches.Select(branch => branch.Path)
            .Should().Equal(NodeTip);
        _mockMsgStore.DidNotReceive().TryClearSelection(Arg.Any<long>(), Arg.Any<SelectedTopicState>());
    }

    [Test]
    public void TurningGroupingOn_KeepsACheckedSelectionHiddenOnlyByCollapse()
    {
        var cut = RenderWithBranch(grouped: true, NodeTip);
        CollapseAll(cut);
        cut.WaitForAssertion(() => cut.FindAll(".topic-tree-row").Should().HaveCount(2));

        ApplyPreference(false);
        ApplyPreference(true);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain(GroupedMarker));

        _mockMsgStore.DidNotReceive().TryClearSelection(Arg.Any<long>(), Arg.Any<SelectedTopicState>());
        ExpandAll(cut);
        cut.WaitForAssertion(() => RowFor(cut, NodeTip).ClassName.Should().Contain("topic-tree-row--selected"));
    }

    [Test]
    public void TurningGroupingOn_KeepsACheckedSelectionHiddenOnlyByTheFilter()
    {
        var cut = RenderTopics(Topics(), grouped: true, branchPath: null);
        cut.Find(".topic-filter-field input").Input("sensors");
        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("NDATA"));

        SeedBranch(NodeTip);
        _mockMsgStore.ClearReceivedCalls();
        ApplyPreference(false);
        ApplyPreference(true);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain(GroupedMarker));

        _mockMsgStore.DidNotReceive().TryClearSelection(Arg.Any<long>(), Arg.Any<SelectedTopicState>());
        cut.Find(".topic-filter-field input").Input(string.Empty);
        ExpandAll(cut);
        cut.WaitForAssertion(() => RowFor(cut, NodeTip).ClassName.Should().Contain("topic-tree-row--selected"));
    }

    [Test]
    public void TurningGroupingOn_KeepsAZeroMessageTipChecked()
    {
        UseRetentionStore();
        (string Topic, int Messages)[] retained =
        [
            (NodeTip, 3),
            ("spBv1.0/plant1/STATE/zero-host", 1)
        ];
        const string kept = "spBv1.0/plant1/STATE/zero-host";

        var cut = RenderTopics(retained, grouped: false, kept);

        ApplyPreference(true);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain(GroupedMarker));
        ExpandAll(cut);
        cut.WaitForAssertion(() => RowFor(cut, kept).ClassName.Should().Contain("topic-tree-row--selected"));
        _mockMsgStore.DidNotReceive().TryClearSelection(Arg.Any<long>(), Arg.Any<SelectedTopicState>());
    }

    [Test]
    [TestCase("spBv1.0")]
    [TestCase("spBv1.0/STATE/scada-host")]
    [TestCase("spBv1.0/plant1/STATE/scada")]
    [TestCase("spBv1.0/plant1/NDATA/node1/extra")]
    [TestCase("sensors/temp")]
    public void TogglingGrouping_KeepsACheckedRawBranchAndItsRow(string branchPath)
    {
        var cut = RenderWithBranch(grouped: false, branchPath);
        ExpandAll(cut);
        CheckedRow(cut, branchPath).Should().BeTrue();

        ApplyPreference(true);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain(GroupedMarker));
        ExpandAll(cut);
        cut.WaitForAssertion(() =>
        {
            CheckedRow(cut, branchPath).Should().BeTrue();
            SelectedBranchPath().Should().Be(branchPath);
        });

        ApplyPreference(false);
        cut.WaitForAssertion(() => cut.Markup.Should().NotContain(GroupedMarker));
        ExpandAll(cut);
        cut.WaitForAssertion(() =>
        {
            CheckedRow(cut, branchPath).Should().BeTrue();
            SelectedBranchPath().Should().Be(branchPath);
        });

        _mockMsgStore.DidNotReceive().SelectTopic(Arg.Any<string?>());
        _mockMsgStore.DidNotReceive().TryClearSelection(Arg.Any<long>(), Arg.Any<SelectedTopicState>());
    }

    [Test]
    public void TurningGroupingOn_KeepsACheckedRetainedFallbackAggregate()
    {
        (string Topic, int Messages)[] mixed =
        [
            (NodeTip, 3),
            ("spBv1.0/plant1/NDATA/ghosttip/leaf", 1),
            ("spBv1.0/plant1/DDATA/node1/dev1", 2)
        ];

        var cut = RenderTopics(mixed, grouped: false, NodeVerb);

        ApplyPreference(true);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain(GroupedMarker));
        ExpandAll(cut);
        cut.WaitForAssertion(() =>
        {
            SelectedBranchPath().Should().Be(NodeVerb);
            CheckedRow(cut, NodeTip).Should().BeTrue("the verb branch still covers the grouped node");
            CheckedRow(cut, "spBv1.0/plant1/NDATA/ghosttip/leaf")
                .Should().BeTrue("the verb branch still covers every topic beneath it");
        });
        RowFor(cut, "ghosttip").ClassName.Should().NotContain("topic-tree-row--group");
        _mockMsgStore.DidNotReceive().SelectTopic(Arg.Any<string?>());
        _mockMsgStore.DidNotReceive().TryClearSelection(Arg.Any<long>(), Arg.Any<SelectedTopicState>());
    }

    [Test]
    public void TurningGroupingOn_KeepsACheckedRetainedChildlessTip()
    {
        UseRetentionStore();
        const string keptNode = "spBv1.0/plant1/NDATA/keptnode";
        (string Topic, int Messages)[] retained =
        [
            (NodeTip, 3),
            (keptNode, 1)
        ];

        var cut = RenderTopics(retained, grouped: false, keptNode);

        ApplyPreference(true);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain(GroupedMarker));
        ExpandAll(cut);
        cut.WaitForAssertion(() =>
        {
            SelectedBranchPath().Should().Be(keptNode);
            CheckedRow(cut, keptNode).Should().BeTrue();
        });
        _mockMsgStore.DidNotReceive().TryClearSelection(Arg.Any<long>(), Arg.Any<SelectedTopicState>());
    }

    [Test]
    public void TurningGroupingOn_WithNoSelectionDoesNotTouchTheStore()
    {
        var cut = RenderTopics(Topics(), grouped: false, branchPath: null);

        ApplyPreference(true);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain(GroupedMarker));

        _mockMsgStore.DidNotReceive().SelectTopic(Arg.Any<string?>());
        _mockMsgStore.DidNotReceive().TryClearSelection(Arg.Any<long>(), Arg.Any<SelectedTopicState>());
        cut.FindAll(".topic-tree-row--selected").Should().BeEmpty();
    }
}
