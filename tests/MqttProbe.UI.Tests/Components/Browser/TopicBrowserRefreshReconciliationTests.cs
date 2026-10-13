using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.UI.Tests.TestHelpers;

namespace MqttProbe.UI.Tests.Components.Browser;

[TestFixture]
public sealed class TopicBrowserRefreshReconciliationTests : BunitTestContext
{
    private const string Aggregate = "spBv1.0/plant1/NDATA";
    private const string Tip = "spBv1.0/plant1/NDATA/node1";
    private const string Residue = "spBv1.0/plant1/NDATA/ghosttip/leaf";
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

    private static ImmutableArray<TopicNodeSnapshot> WithResidue() => TopicSnapshotBuilder.Build(
        (Tip, 3),
        (Residue, 1));

    private static ImmutableArray<TopicNodeSnapshot> WithoutResidue() => TopicSnapshotBuilder.Build((Tip, 3));

    private IRenderedComponent<TopicBrowser> RenderSelected(
        ImmutableArray<TopicNodeSnapshot> tree,
        string selectedPath,
        bool grouped = true)
    {
        _preferences.GroupSparkplugTopicsByNodeAndDevice = grouped;
        _fixture.SetSnapshot([.. tree]);
        _fixture.SetSelection(new SelectedTopicState(new SelectedTopicToken(_storeId, 1, 1), selectedPath, 1));
        var cut = Render<TopicBrowser>();
        EnsureMudProviders();
        return cut;
    }

    // Waits for the component's own refresh to read the store, then for the render it causes.
    private async Task<IRenderedComponent<TopicBrowser>> RefreshAfterAsync(
        IRenderedComponent<TopicBrowser> cut,
        ImmutableArray<TopicNodeSnapshot> tree,
        CancellationToken token)
    {
        var read = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var armed = true;
        _fixture.SetSnapshot([.. tree]);
        _mockMsgStore.GetTopicTreeSnapshot().Returns(_ =>
        {
            if (Interlocked.Exchange(ref armed, false))
                read.TrySetResult(true);
            return _fixture.Snapshot;
        });

        await read.Task.WaitAsync(token);
        return cut;
    }

    private static void ExpandAll(IRenderedComponent<TopicBrowser> cut) =>
        cut.FindAll(".mud-toolbar button")[0].Click();

    [Test]
    public async Task VersionChange_ThatRemovesTheOnlyResidue_ClearsTheRemovedAggregate()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var cut = RenderSelected(WithResidue(), Aggregate);
        ExpandAll(cut);
        cut.FindAll(".topic-tree-row--selected").Should().ContainSingle(
            "the residual prefix has a row to start with");

        await RefreshAfterAsync(cut, WithoutResidue(), timeout.Token);

        cut.WaitForAssertion(() => _mockMsgStore.Received(1)
            .TryClearSelection(Arg.Any<long>(), Arg.Any<SelectedTopicState>()));
        cut.WaitForAssertion(() => cut.FindAll(".topic-tree-row--selected").Should().BeEmpty());
        _mockMsgStore.DidNotReceive().SelectTopic(Arg.Any<string?>());
        _fixture.SelectedFullTopic.Should().BeNull("the clear reached the manager");
        cut.Markup.Should().Contain("spBv1.0/plant1/NDATA/node1",
            "the valid tip under the same verb stays grouped");
    }

    [Test]
    public async Task VersionChange_KeepsTheSelectedLeafThatIsStillPresent()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var cut = RenderSelected(WithResidue(), Tip);

        await RefreshAfterAsync(cut, WithoutResidue(), timeout.Token);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain(GroupedMarker));
        _mockMsgStore.DidNotReceive().SelectTopic(Arg.Any<string?>());
        _mockMsgStore.DidNotReceive().TryClearSelection(Arg.Any<long>(), Arg.Any<SelectedTopicState>());
        _fixture.SelectedFullTopic.Should().Be(Tip);
        ExpandAll(cut);
        cut.WaitForAssertion(() =>
        {
            var selected = cut.FindAll(".topic-tree-row--selected");
            selected.Should().ContainSingle();
            selected[0].TextContent.Should().Contain(Tip);
        });
    }

    [Test]
    public async Task VersionChange_KeepsTheRetainedFallbackPrefixWhileItsResidueRemains()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var cut = RenderSelected(WithResidue(), Aggregate);

        await RefreshAfterAsync(cut, WithResidue(), timeout.Token);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain(GroupedMarker));
        _mockMsgStore.DidNotReceive().SelectTopic(Arg.Any<string?>());
        _mockMsgStore.DidNotReceive().TryClearSelection(Arg.Any<long>(), Arg.Any<SelectedTopicState>());
        _fixture.SelectedFullTopic.Should().Be(Aggregate);
    }

    [Test]
    public void FilterAndCollapseRebuilds_DoNotClearAHiddenLeaf()
    {
        var cut = RenderSelected(WithResidue(), Tip);

        // The filter clears the selection by design, so it is set again behind the filter.
        cut.Find(".topic-filter-field input").Input("industrial");
        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("Node messages"));
        _fixture.SetSelection(new SelectedTopicState(new SelectedTopicToken(_storeId, 2, 2), Tip, 3));
        _mockMsgStore.ClearReceivedCalls();

        cut.FindAll(".mud-toolbar button")[1].Click(); // collapse all

        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("Node messages"));
        _mockMsgStore.DidNotReceive().SelectTopic(Arg.Any<string?>());
        _mockMsgStore.DidNotReceive().TryClearSelection(Arg.Any<long>(), Arg.Any<SelectedTopicState>());
        _fixture.SelectedFullTopic.Should().Be(Tip);
    }

    [Test]
    public void StaleVerdict_WhenTheStoreMovesUnderTheClear_KeepsTheSelectionForTheNextRefresh()
    {
        // The verdict sees an emptied store, but the topic returns at the instant of the clear.
        // The conditional clear must reject, and the next ordinary refresh, with no ExpandAll,
        // must render the row it never cleared.
        _preferences.GroupSparkplugTopicsByNodeAndDevice = false;
        _fixture.SetSnapshot();
        _fixture.SetSelection(new SelectedTopicState(new SelectedTopicToken(_storeId, 1, 1), "sensors", 3));
        _fixture.BeforeConditionalClear =
            () => _fixture.SetSnapshot([.. TopicSnapshotBuilder.Build(("sensors/temp", 2))]);

        SelectedTopicState? callbackState = null;
        var cut = Render<TopicBrowser>(parameters => parameters.Add(
            component => component.OnSelectedChanged, state =>
            {
                callbackState = state;
                return Task.CompletedTask;
            }));
        EnsureMudProviders();

        cut.WaitForAssertion(() => _mockMsgStore.Received(1)
            .TryClearSelection(Arg.Any<long>(), Arg.Any<SelectedTopicState>()));
        _mockMsgStore.DidNotReceive().SelectTopic(Arg.Any<string?>());
        callbackState.Should().BeNull("a rejected clear must not raise the selection callback");
        _fixture.SelectedFullTopic.Should().Be("sensors");

        cut.WaitForAssertion(() =>
        {
            var selected = cut.FindAll(".topic-tree-row--selected");
            selected.Should().ContainSingle();
            selected[0].TextContent.Should().Contain("sensors");
        }, TimeSpan.FromSeconds(10));
    }

    [Test]
    public void StaleVerdict_WhenTheSelectionMovesUnderTheClear_KeepsTheNewSelection()
    {
        // The tree stays purged, but a new selection arrives before the clear is evaluated, so
        // the captured token no longer matches and the clear must leave the live selection alone.
        _preferences.GroupSparkplugTopicsByNodeAndDevice = true;
        _fixture.SetSnapshot([.. WithoutResidue()]);
        _fixture.SetSelection(new SelectedTopicState(new SelectedTopicToken(_storeId, 1, 1), Aggregate, 1));
        _fixture.BeforeConditionalClear = () => _fixture.SetSelection(
            new SelectedTopicState(new SelectedTopicToken(_storeId, 9, 9), Tip, 3));

        var cut = Render<TopicBrowser>();
        EnsureMudProviders();
        ExpandAll(cut);

        cut.WaitForAssertion(() => _mockMsgStore.Received(1)
            .TryClearSelection(Arg.Any<long>(), Arg.Any<SelectedTopicState>()));
        _mockMsgStore.DidNotReceive().SelectTopic(Arg.Any<string?>());
        _fixture.SelectedFullTopic.Should().Be(Tip);
        cut.WaitForAssertion(() =>
        {
            var selected = cut.FindAll(".topic-tree-row--selected");
            selected.Should().ContainSingle();
            selected[0].TextContent.Should().Contain(Tip);
        });
    }

    [Test]
    public void PreservedSelection_OnARebuild_KeepsTheSelectionAndTouchesNothing()
    {
        var cut = RenderSelected(WithResidue(), Tip);

        ExpandAll(cut);

        cut.WaitForAssertion(() => cut.FindAll(".topic-tree-row--selected").Should().ContainSingle());
        _mockMsgStore.DidNotReceive().SelectTopic(Arg.Any<string?>());
        _mockMsgStore.DidNotReceive().TryClearSelection(Arg.Any<long>(), Arg.Any<SelectedTopicState>());
        _fixture.SelectedFullTopic.Should().Be(Tip);
    }

    [Test]
    public async Task UnchangedVersion_DoesNotTouchTheStore()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var cut = RenderSelected(WithResidue(), Tip);

        await RefreshAfterAsync(cut, WithResidue(), timeout.Token);

        _mockMsgStore.DidNotReceive().SelectTopic(Arg.Any<string?>());
        _mockMsgStore.DidNotReceive().TryClearSelection(Arg.Any<long>(), Arg.Any<SelectedTopicState>());
    }

    [Test]
    public void LiteralMode_KeepsAPresentLeafSelection()
    {
        var cut = RenderSelected(WithResidue(), Residue, grouped: false);
        ExpandAll(cut);
        cut.FindAll(".topic-tree-row--selected").Should().ContainSingle();

        cut.FindAll(".mud-toolbar button")[1].Click(); // collapse all
        ExpandAll(cut);

        cut.WaitForAssertion(() => cut.FindAll(".topic-tree-row--selected").Should().ContainSingle());
        _mockMsgStore.DidNotReceive().SelectTopic(Arg.Any<string?>());
        _mockMsgStore.DidNotReceive().TryClearSelection(Arg.Any<long>(), Arg.Any<SelectedTopicState>());
    }
}
