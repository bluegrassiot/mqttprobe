using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.UI.Tests.TestHelpers;

namespace MqttProbe.UI.Tests.Components.Browser;

// A refresh reconciles the checked set against the store: a branch whose rows are gone is dropped
// and everything else stays exactly as the user left it.
[TestFixture]
public sealed class TopicBrowserRefreshReconciliationTests : BunitTestContext
{
    private const string Aggregate = "spBv1.0/plant1/NDATA";
    private const string Tip = "spBv1.0/plant1/NDATA/node1";
    private const string Residue = "spBv1.0/plant1/NDATA/ghosttip/leaf";

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

    private IRenderedComponent<TopicBrowser> RenderSelected(string selectedPath, bool grouped = false)
    {
        _preferences.GroupSparkplugTopicsByNodeAndDevice = grouped;
        Add(Tip, 3);
        Add(Residue, 1);
        SeedBranch(selectedPath);
        var cut = Render<TopicBrowser>();
        EnsureMudProviders();
        return cut;
    }

    private void SeedBranch(string path) =>
        _fixture.Adapter.Store.SelectTopics(
            TopicSelection.ForBranches([new TopicBranchTarget(TopicBranchKind.Raw, Path: path)]));

    private void Add(string topic, int count)
    {
        for (var index = 0; index < count; index++)
            _fixture.Adapter.Add(topic, new MqttMessage($"payload-{index}", "unused", false,
                MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce)
            { Topic = topic });
    }

    private static void ExpandAll(IRenderedComponent<TopicBrowser> cut) =>
        cut.FindAll(".mud-toolbar button")[0].Click();

    private static IReadOnlyList<string> CheckedPaths(IRenderedComponent<TopicBrowser> cut) =>
        [.. cut.FindAll(".topic-tree-row--selected").Select(row => row.GetAttribute("data-topic-path")!)];

    [Test]
    public async Task Purge_KeepsACheckedBranchThatStillHasRows()
    {
        var cut = RenderSelected(Aggregate);
        ExpandAll(cut);
        CheckedPaths(cut).Should().Contain(Aggregate);

        _fixture.Adapter.Store.RemoveMatchingTopic("spBv1.0/plant1/NDATA/ghosttip/#");
        await cut.InvokeAsync(() => cut.Instance.RefreshAsync());

        cut.WaitForAssertion(() =>
        {
            CheckedPaths(cut).Should().Contain(Aggregate,
                "the branch still has the valid tip under it");
            _fixture.Adapter.Store.GetSelectedTopicState().Branches.Select(branch => branch.Path)
                .Should().Equal(Aggregate);
        });
        _mockMsgStore.DidNotReceive().SelectTopic(Arg.Any<string?>());
        _mockMsgStore.DidNotReceive().TryClearSelection(Arg.Any<long>(), Arg.Any<SelectedTopicState>());
    }

    [Test]
    public async Task Purge_DropsACheckedBranchWhenItsRowsAreGone()
    {
        var cut = RenderSelected(Aggregate);
        ExpandAll(cut);

        _fixture.Adapter.Store.RemoveMatchingTopic("spBv1.0/plant1/NDATA/#");
        await cut.InvokeAsync(() => cut.Instance.RefreshAsync());

        cut.WaitForAssertion(() =>
        {
            CheckedPaths(cut).Should().BeEmpty("the branch no longer names anything");
            _fixture.Adapter.Store.GetSelectedTopicState().Key.Should().BeNull();
        });
        _mockMsgStore.DidNotReceive().SelectTopic(Arg.Any<string?>());
    }

    [Test]
    public async Task Purge_OfOneBranch_KeepsAnother()
    {
        _preferences.GroupSparkplugTopicsByNodeAndDevice = false;
        Add(Tip, 3);
        Add("sensors/temp", 1);
        SeedBranch(Aggregate);
        SeedBranch("sensors");
        var cut = Render<TopicBrowser>();
        EnsureMudProviders();
        ExpandAll(cut);

        _fixture.Adapter.Store.RemoveMatchingTopic("spBv1.0/plant1/NDATA/#");
        await cut.InvokeAsync(() => cut.Instance.RefreshAsync());

        cut.WaitForAssertion(() =>
        {
            _fixture.Adapter.Store.GetSelectedTopicState().Branches.Select(branch => branch.Path)
                .Should().Equal("sensors");
            CheckedPaths(cut).Should().Contain("sensors");
            CheckedPaths(cut).Should().NotContain(path => path.StartsWith("spBv1.0/", StringComparison.Ordinal));
        });
    }

    [Test]
    public void FilterAndCollapseRebuilds_DoNotDropACheckedBranch()
    {
        var cut = RenderSelected(Tip);
        cut.Find(".topic-filter-field input").Input("industrial");
        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("NDATA"));
        _mockMsgStore.ClearReceivedCalls();

        cut.FindAll(".mud-toolbar button")[1].Click();

        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("NDATA"));
        _mockMsgStore.DidNotReceive().SelectTopic(Arg.Any<string?>());
        _mockMsgStore.DidNotReceive().TryClearSelection(Arg.Any<long>(), Arg.Any<SelectedTopicState>());
        _fixture.Adapter.Store.GetSelectedTopicState().Branches.Select(branch => branch.Path)
            .Should().Equal(Tip);
    }

    [Test]
    public void PreservedSelection_OnARebuild_KeepsTheSelectionAndTouchesNothing()
    {
        var cut = RenderSelected(Tip);

        ExpandAll(cut);

        cut.WaitForAssertion(() => CheckedPaths(cut).Should().ContainSingle().Which.Should().Be(Tip));
        _mockMsgStore.DidNotReceive().SelectTopic(Arg.Any<string?>());
        _mockMsgStore.DidNotReceive().TryClearSelection(Arg.Any<long>(), Arg.Any<SelectedTopicState>());
    }

    [Test]
    public async Task UnchangedVersion_DoesNotTouchTheStore()
    {
        var cut = RenderSelected(Tip);
        var version = _fixture.Adapter.Store.Version;

        await cut.InvokeAsync(() => cut.Instance.RefreshAsync());

        _fixture.Adapter.Store.Version.Should().Be(version);
        _mockMsgStore.DidNotReceive().SelectTopic(Arg.Any<string?>());
        _mockMsgStore.DidNotReceive().TryClearSelection(Arg.Any<long>(), Arg.Any<SelectedTopicState>());
    }

    [Test]
    public void LiteralMode_KeepsAPresentLeafSelection()
    {
        var cut = RenderSelected(Residue, grouped: false);
        ExpandAll(cut);
        CheckedPaths(cut).Should().ContainSingle().Which.Should().Be(Residue);

        cut.FindAll(".mud-toolbar button")[1].Click();
        ExpandAll(cut);

        cut.WaitForAssertion(() => CheckedPaths(cut).Should().ContainSingle().Which.Should().Be(Residue));
        _mockMsgStore.DidNotReceive().SelectTopic(Arg.Any<string?>());
    }
}
