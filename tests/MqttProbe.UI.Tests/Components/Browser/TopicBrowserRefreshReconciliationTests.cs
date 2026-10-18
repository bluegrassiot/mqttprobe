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
        Services.AddSingleton<ITopicSelectionStore>(_fixture.Adapter);
        _mockUi = SettingsSubstituteRegistration.CreateUiSettings(_preferences);
        Services.AddSingleton(_mockMsgStore);
        Services.AddUiSettings(_mockUi);
    }

    private IRenderedComponent<TopicBrowser> RenderSelected(
        string selectedPath,
        bool grouped = true)
    {
        _preferences.GroupSparkplugTopicsByNodeAndDevice = grouped;
        Add("spBv1.0/plant1/NDATA/node1", 3);
        Add(Residue, 1);
        _fixture.SetSelection(TopicSelection.ForTopic(selectedPath));
        var cut = Render<TopicBrowser>();
        EnsureMudProviders();
        return cut;
    }

    // Waits for the component's own refresh to read the store, then for the render it causes.
    private async Task<IRenderedComponent<TopicBrowser>> RefreshAsync(
        IRenderedComponent<TopicBrowser> cut,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await cut.InvokeAsync(() => cut.Instance.RefreshAsync());
        return cut;
    }

    private void Add(string topic, int count)
    {
        for (var index = 0; index < count; index++)
            _fixture.Adapter.Add(topic, new MqttMessage($"payload-{index}", "unused", false,
                MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce)
            { Topic = topic });
    }

    private static void ExpandAll(IRenderedComponent<TopicBrowser> cut) =>
        cut.FindAll(".mud-toolbar button")[0].Click();

    [Test]
    public async Task VersionChange_ThatRemovesTheOnlyResidue_ClearsTheRemovedAggregate()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var cut = RenderSelected(Aggregate);
        ExpandAll(cut);
        cut.FindAll(".topic-tree-row--selected").Should().ContainSingle(
            "the residual prefix has a row to start with");

        _fixture.Adapter.Store.RemoveMatchingTopic("spBv1.0/plant1/NDATA/ghosttip/#");
        await RefreshAsync(cut, timeout.Token);

        cut.WaitForAssertion(() => _mockMsgStore.Received(1)
            .TryClearSelection(Arg.Any<long>(), Arg.Any<SelectedTopicState>()));
        cut.WaitForAssertion(() => cut.FindAll(".topic-tree-row--selected").Should().BeEmpty());
        _mockMsgStore.DidNotReceive().SelectTopic(Arg.Any<string?>());
        _fixture.SelectedKey.Should().BeNull("the clear reached the manager");
        cut.Markup.Should().Contain("spBv1.0/plant1/NDATA/node1",
            "the valid tip under the same verb stays grouped");
    }

    [Test]
    public async Task VersionChange_KeepsTheSelectedLeafThatIsStillPresent()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var cut = RenderSelected(Tip);

        _fixture.Adapter.Store.RemoveMatchingTopic("spBv1.0/plant1/NDATA/ghosttip/#");
        await RefreshAsync(cut, timeout.Token);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain(GroupedMarker));
        _mockMsgStore.DidNotReceive().SelectTopic(Arg.Any<string?>());
        _mockMsgStore.DidNotReceive().TryClearSelection(Arg.Any<long>(), Arg.Any<SelectedTopicState>());
        _fixture.SelectedKey.Should().Be(Tip);
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
        var cut = RenderSelected(Aggregate);

        Add(Tip, 1);
        await RefreshAsync(cut, timeout.Token);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain(GroupedMarker));
        _mockMsgStore.DidNotReceive().SelectTopic(Arg.Any<string?>());
        _mockMsgStore.DidNotReceive().TryClearSelection(Arg.Any<long>(), Arg.Any<SelectedTopicState>());
        _fixture.SelectedKey.Should().Be(Aggregate);
    }

    [Test]
    public void FilterAndCollapseRebuilds_DoNotClearAHiddenLeaf()
    {
        var cut = RenderSelected(Tip);

        // The filter clears the selection by design, so it is set again behind the filter.
        cut.Find(".topic-filter-field input").Input("industrial");
        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("NDATA"));
        _fixture.SetSelection(TopicSelection.ForTopic(Tip));
        _mockMsgStore.ClearReceivedCalls();

        cut.FindAll(".mud-toolbar button")[1].Click(); // collapse all

        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("NDATA"));
        _mockMsgStore.DidNotReceive().SelectTopic(Arg.Any<string?>());
        _mockMsgStore.DidNotReceive().TryClearSelection(Arg.Any<long>(), Arg.Any<SelectedTopicState>());
        _fixture.SelectedKey.Should().Be(Tip);
    }

    [Test]
    public async Task StaleVerdict_WhenTheStoreMovesUnderTheClear_KeepsTheSelectionForTheNextRefresh()
    {
        // The verdict sees an emptied store, but the topic returns at the instant of the clear.
        // The conditional clear must reject, and the next ordinary refresh, with no ExpandAll,
        // must render the row it never cleared.
        _preferences.GroupSparkplugTopicsByNodeAndDevice = false;
        Add("sensors/temp", 2);
        _fixture.SetSelection(TopicSelection.ForTopic("sensors"));
        _fixture.Adapter.ReturnSnapshotOnce(
            new TopicTreeSnapshot(_fixture.Adapter.Store.Version, ImmutableArray<TopicNodeSnapshot>.Empty));
        _fixture.BeforeConditionalClear = () => Add("sensors/temp", 1);

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
        _fixture.SelectedKey.Should().Be("sensors");

        await cut.InvokeAsync(() => cut.Instance.RefreshAsync());
        var selected = cut.FindAll(".topic-tree-row--selected");
        selected.Should().ContainSingle();
        selected[0].TextContent.Should().Contain("sensors");
    }

    [Test]
    public async Task PurgeAfterSelectionConfirmation_DoesNotPublishTheStaleSelection()
    {
        Add("sensors/temp", 1);
        SelectedTopicState? callbackState = null;
        var cut = Render<TopicBrowser>(parameters => parameters.Add(
            component => component.OnSelectedChanged, state =>
            {
                callbackState = state;
                return Task.CompletedTask;
            }));
        EnsureMudProviders();
        _fixture.Adapter.BeforeSnapshotRead = () => _fixture.Adapter.Store.RemoveMatchingTopic("sensors/#");

        await cut.InvokeAsync(() => cut.Find(".topic-tree-row").Click());

        callbackState.Should().BeNull();
        _fixture.SelectedKey.Should().BeNull();
        cut.FindAll(".topic-tree-row--selected").Should().BeEmpty();
    }

    [Test]
    public void StaleVerdict_WhenTheSelectionMovesUnderTheClear_KeepsTheNewSelection()
    {
        // The tree stays purged, but a new selection arrives before the clear is evaluated, so
        // the captured token no longer matches and the clear must leave the live selection alone.
        _preferences.GroupSparkplugTopicsByNodeAndDevice = true;
        Add(Tip, 3);
        _fixture.SetSelection(TopicSelection.ForTopic(Aggregate));
        _fixture.Adapter.ReturnSnapshotOnce(
            new TopicTreeSnapshot(_fixture.Adapter.Store.Version, ImmutableArray<TopicNodeSnapshot>.Empty));
        _fixture.BeforeConditionalClear = () => _fixture.SetSelection(TopicSelection.ForTopic(Tip));

        var cut = Render<TopicBrowser>();
        EnsureMudProviders();
        ExpandAll(cut);

        cut.WaitForAssertion(() => _mockMsgStore.Received(1)
            .TryClearSelection(Arg.Any<long>(), Arg.Any<SelectedTopicState>()));
        _mockMsgStore.DidNotReceive().SelectTopic(Arg.Any<string?>());
        _fixture.SelectedKey.Should().Be(Tip);
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
        var cut = RenderSelected(Tip);

        ExpandAll(cut);

        cut.WaitForAssertion(() => cut.FindAll(".topic-tree-row--selected").Should().ContainSingle());
        _mockMsgStore.DidNotReceive().SelectTopic(Arg.Any<string?>());
        _mockMsgStore.DidNotReceive().TryClearSelection(Arg.Any<long>(), Arg.Any<SelectedTopicState>());
        _fixture.SelectedKey.Should().Be(Tip);
    }

    [Test]
    public async Task UnchangedVersion_DoesNotTouchTheStore()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var cut = RenderSelected(Tip);
        var version = _fixture.Adapter.Store.Version;

        await RefreshAsync(cut, timeout.Token);

        _fixture.Adapter.Store.Version.Should().Be(version);
        _mockMsgStore.DidNotReceive().SelectTopic(Arg.Any<string?>());
        _mockMsgStore.DidNotReceive().TryClearSelection(Arg.Any<long>(), Arg.Any<SelectedTopicState>());
    }

    [Test]
    public void LiteralMode_KeepsAPresentLeafSelection()
    {
        var cut = RenderSelected(Residue, grouped: false);
        ExpandAll(cut);
        cut.FindAll(".topic-tree-row--selected").Should().ContainSingle();

        cut.FindAll(".mud-toolbar button")[1].Click(); // collapse all
        ExpandAll(cut);

        cut.WaitForAssertion(() => cut.FindAll(".topic-tree-row--selected").Should().ContainSingle());
        _mockMsgStore.DidNotReceive().SelectTopic(Arg.Any<string?>());
        _mockMsgStore.DidNotReceive().TryClearSelection(Arg.Any<long>(), Arg.Any<SelectedTopicState>());
    }
}
