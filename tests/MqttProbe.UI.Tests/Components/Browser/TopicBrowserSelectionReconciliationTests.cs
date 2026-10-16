using AngleSharp.Dom;
using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.UI.Tests.TestHelpers;

namespace MqttProbe.UI.Tests.Components.Browser;

[TestFixture]
public sealed class TopicBrowserSelectionReconciliationTests : BunitTestContext
{
    private const string NodeTip = "spBv1.0/plant1/NDATA/node1";
    private const string NodeVerb = "spBv1.0/plant1/NDATA";
    private const string DeviceLevel = "spBv1.0/plant1/DDATA/node1";
    private const string GroupedMarker = "Grouped by node and device";

    private IMessageStoreManager _mockMsgStore = null!;
    private TopicBrowserTestFixture _fixture = null!;
    private IUiSettings _mockUi = null!;
    private readonly UiPreferences _preferences = new();
    private readonly Guid _storeId = Guid.NewGuid();

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

    private static TopicNodeSnapshot[] Tree() =>
    [
        .. TopicSnapshotBuilder.Build(
            (NodeTip, 3),
            ("spBv1.0/plant1/NDATA/node1/extra", 1),
            ("spBv1.0/plant1/NBIRTH/node1", 1),
            ("spBv1.0/plant1/DDATA/node1/dev1", 2),
            ("spBv1.0/plant1/STATE/scada", 1),
            ("spBv1.0/STATE/scada-host", 1),
            ("sensors/temp", 2))
    ];

    private IRenderedComponent<TopicBrowser> RenderWithSelection(
        bool grouped,
        string? selectedPath,
        Func<SelectedTopicState, Task>? onSelected = null) =>
        RenderSnapshot(Tree(), grouped, selectedPath, onSelected);

    private IRenderedComponent<TopicBrowser> RenderSnapshot(
        TopicNodeSnapshot[] tree,
        bool grouped,
        string? selectedPath,
        Func<SelectedTopicState, Task>? onSelected = null)
    {
        _preferences.GroupSparkplugTopicsByNodeAndDevice = grouped;
        _fixture.SetSnapshot(tree);
        if (selectedPath is not null)
            _fixture.SetSelection(new SelectedTopicState(new SelectedTopicToken(_storeId, 1, 1), selectedPath, 1));
        var cut = Render<TopicBrowser>(parameters =>
        {
            if (onSelected is not null)
                parameters.Add(component => component.OnSelectedChanged, onSelected);
        });
        EnsureMudProviders();
        return cut;
    }

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

    [Test]
    public void TurningGroupingOn_ClearsASelectionTheGroupedViewCannotShow()
    {
        SelectedTopicState? callbackState = null;
        var cut = RenderWithSelection(grouped: false, NodeVerb, state =>
        {
            callbackState = state;
            return Task.CompletedTask;
        });
        ExpandAll(cut);
        RowFor(cut, "NDATA").ClassName.Should().Contain("topic-tree-row--selected");

        ApplyPreference(true);

        cut.WaitForAssertion(() => _mockMsgStore.Received(1)
            .TryClearSelection(Arg.Any<long>(), Arg.Any<SelectedTopicState>()));
        _mockMsgStore.DidNotReceive().SelectTopic(Arg.Any<string?>());
        callbackState.Should().BeNull("the preference change must not raise the selection callback");
        cut.WaitForAssertion(() => cut.FindAll(".topic-tree-row--selected").Should().BeEmpty());
    }

    [Test]
    public void TurningGroupingOn_ClearsADeviceIntermediateSelection()
    {
        var cut = RenderWithSelection(grouped: false, DeviceLevel);
        ExpandAll(cut);

        ApplyPreference(true);

        cut.WaitForAssertion(() => _mockMsgStore.Received(1)
            .TryClearSelection(Arg.Any<long>(), Arg.Any<SelectedTopicState>()));
        _mockMsgStore.DidNotReceive().SelectTopic(Arg.Any<string?>());
        cut.FindAll(".topic-tree-row--selected").Should().BeEmpty();
    }

    [Test]
    public void TurningGroupingOn_KeepsASelectionHiddenOnlyByCollapse()
    {
        var cut = RenderWithSelection(grouped: true, NodeTip);

        // Collapsed: the row is off screen, but the topic is still represented.
        CollapseAll(cut);
        cut.WaitForAssertion(() => cut.FindAll(".topic-tree-row").Should().HaveCount(2));

        ApplyPreference(false);
        ApplyPreference(true);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain(GroupedMarker));

        _mockMsgStore.DidNotReceive().SelectTopic(Arg.Any<string?>());
        _mockMsgStore.DidNotReceive().TryClearSelection(Arg.Any<long>(), Arg.Any<SelectedTopicState>());
        ExpandAll(cut);
        cut.WaitForAssertion(() => RowFor(cut, NodeTip).ClassName.Should().Contain("topic-tree-row--selected"));
    }

    [Test]
    public void TurningGroupingOn_KeepsASelectionHiddenOnlyByTheFilter()
    {
        var cut = RenderWithSelection(grouped: true, selectedPath: null);

        // The filter clears selections by design, so this one is set behind the filter and the
        // toggle must leave it alone.
        cut.Find(".topic-filter-field input").Input("sensors");
        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("NDATA"));
        _fixture.SetSelection(new SelectedTopicState(new SelectedTopicToken(_storeId, 2, 2), NodeTip, 3));
        _mockMsgStore.ClearReceivedCalls();

        ApplyPreference(false);
        ApplyPreference(true);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain(GroupedMarker));

        _mockMsgStore.DidNotReceive().SelectTopic(Arg.Any<string?>());
        _mockMsgStore.DidNotReceive().TryClearSelection(Arg.Any<long>(), Arg.Any<SelectedTopicState>());
        _fixture.SelectedKey.Should().Be(NodeTip);
    }

    [TestCase("spBv1.0")]
    [TestCase("spBv1.0/plant1")]
    [TestCase("spBv1.0/STATE/scada-host")]
    [TestCase("spBv1.0/plant1/STATE/scada")]
    [TestCase("spBv1.0/plant1/NDATA/node1/extra")]
    [TestCase("sensors/temp")]
    public void TurningGroupingOn_KeepsSelectionsTheGroupedViewStillShows(string selectedPath)
    {
        var cut = RenderWithSelection(grouped: false, selectedPath);

        ApplyPreference(true);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain(GroupedMarker));

        _mockMsgStore.DidNotReceive().SelectTopic(Arg.Any<string?>());
        _mockMsgStore.DidNotReceive().TryClearSelection(Arg.Any<long>(), Arg.Any<SelectedTopicState>());
    }

    [Test]
    public void TurningGroupingOff_KeepsTheSelectionForALiteralRow()
    {
        var cut = RenderWithSelection(grouped: true, NodeTip);

        ApplyPreference(false);
        cut.WaitForAssertion(() => cut.Markup.Should().NotContain(GroupedMarker));

        _mockMsgStore.DidNotReceive().SelectTopic(Arg.Any<string?>());
        _mockMsgStore.DidNotReceive().TryClearSelection(Arg.Any<long>(), Arg.Any<SelectedTopicState>());
        ExpandAll(cut);
        cut.WaitForAssertion(() => cut.FindAll(".topic-tree-row--selected").Should().NotBeEmpty());
    }

    [Test]
    public void TurningGroupingOn_KeepsTheSelectionForARetainedFallbackAggregate()
    {
        TopicNodeSnapshot[] mixed =
        [
            .. TopicSnapshotBuilder.Build(
                (NodeTip, 3),
                ("spBv1.0/plant1/NDATA/ghosttip/leaf", 1),
                ("spBv1.0/plant1/DDATA/node1/dev1", 2))
        ];

        var cut = RenderSnapshot(mixed, grouped: false, NodeVerb);

        ApplyPreference(true);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain(GroupedMarker));

        _mockMsgStore.DidNotReceive().SelectTopic(Arg.Any<string?>());
        _mockMsgStore.DidNotReceive().TryClearSelection(Arg.Any<long>(), Arg.Any<SelectedTopicState>());
        _fixture.SelectedKey.Should().Be(NodeVerb);
        ExpandAll(cut);
        cut.WaitForAssertion(() =>
        {
            var selected = cut.FindAll(".topic-tree-row--selected");
            selected.Should().ContainSingle();
            selected[0].TextContent.Should().Contain("NDATA");
            selected[0].TextContent.Should().NotContain("ghosttip");
        });
        RowFor(cut, "ghosttip").ClassName.Should().NotContain("topic-tree-row--group");
    }

    [Test]
    public void TurningGroupingOn_KeepsTheSelectionForARetainedZeroMessageTopic()
    {
        TopicNodeSnapshot[] retained =
        [
            .. TopicSnapshotBuilder.Build(
                (NodeTip, 3),
                ("spBv1.0/plant1/STATE/zero-host", 0))
        ];

        var cut = RenderSnapshot(retained, grouped: false, "spBv1.0/plant1/STATE/zero-host");

        ApplyPreference(true);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain(GroupedMarker));

        _mockMsgStore.DidNotReceive().SelectTopic(Arg.Any<string?>());
        _mockMsgStore.DidNotReceive().TryClearSelection(Arg.Any<long>(), Arg.Any<SelectedTopicState>());
        _fixture.SelectedKey.Should().Be("spBv1.0/plant1/STATE/zero-host");
        ExpandAll(cut);
        cut.WaitForAssertion(() =>
        {
            var selected = cut.FindAll(".topic-tree-row--selected");
            selected.Should().ContainSingle();
            selected[0].TextContent.Should().Contain("zero-host");
        });
    }

    [Test]
    public void TurningGroupingOn_KeepsTheSelectionForARetainedChildlessTip()
    {
        const string keptNode = "spBv1.0/plant1/NDATA/keptnode";
        TopicNodeSnapshot[] retained =
        [
            .. TopicSnapshotBuilder.Build(
                (NodeTip, 3),
                (keptNode, 0))
        ];

        var cut = RenderSnapshot(retained, grouped: false, keptNode);

        ApplyPreference(true);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain(GroupedMarker));

        _mockMsgStore.DidNotReceive().SelectTopic(Arg.Any<string?>());
        _mockMsgStore.DidNotReceive().TryClearSelection(Arg.Any<long>(), Arg.Any<SelectedTopicState>());
        _fixture.SelectedKey.Should().Be(keptNode);
        ExpandAll(cut);
        cut.WaitForAssertion(() =>
        {
            var selected = cut.FindAll(".topic-tree-row--selected");
            selected.Should().ContainSingle();
            selected[0].TextContent.Should().Contain(keptNode);
        });
    }

    [Test]
    public void TurningGroupingOn_WithNoSelectionDoesNotTouchTheStore()
    {
        var cut = RenderWithSelection(grouped: false, selectedPath: null);

        ApplyPreference(true);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain(GroupedMarker));

        _mockMsgStore.DidNotReceive().SelectTopic(Arg.Any<string?>());
        _mockMsgStore.DidNotReceive().TryClearSelection(Arg.Any<long>(), Arg.Any<SelectedTopicState>());
    }
}
