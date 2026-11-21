using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.UI.Tests.TestHelpers;

namespace MqttProbe.UI.Tests.Components.Browser;

// The tree keeps one roving Tab stop. Until the user or the page chooses a row it belongs to the
// first visible row, so Tab always reaches the top of the tree rather than a checked branch or a
// row that used to be first before another root sorted above it.
[TestFixture]
public sealed class TopicBrowserTabStopInitializationTests : BunitTestContext
{
    private IMessageStoreManager _mockMsgStore = null!;
    private TopicBrowserTestFixture _fixture = null!;
    private readonly UiPreferences _preferences = new();

    [SetUp]
    public void SetupMocks()
    {
        _mockMsgStore = Substitute.For<IMessageStoreManager>();
        _fixture = new TopicBrowserTestFixture(_mockMsgStore);
        Services.AddSingleton<ITopicSelectionStore>(_fixture.Adapter);
        Services.AddUiSettings(SettingsSubstituteRegistration.CreateUiSettings(_preferences));
    }

    [Test]
    public async Task GroupedTree_NewRootAboveTheNamespace_LeavesTheTabStopOnTheFirstRow()
    {
        _preferences.GroupSparkplugTopicsByNodeAndDevice = true;
        _fixture.AddTopics(("spBv1.0/plant/NDATA/gw1", 1));
        var cut = Render<TopicBrowser>();
        EnsureMudProviders();
        TabStopKey(cut).Should().Be("spBv1.0");

        _fixture.AddTopics(("alpha/one", 1));
        await cut.InvokeAsync(() => cut.Instance.RefreshAsync());

        cut.WaitForAssertion(() =>
        {
            FirstRowKey(cut).Should().Be("alpha");
            TabStopKey(cut).Should().Be("alpha");
        });
        cut.Find(".topic-tree-row[data-topic-key='spBv1.0']").GetAttribute("tabindex").Should().Be("-1");
    }

    [Test]
    public async Task LiteralTree_NewRootAboveTheFirstRow_LeavesTheTabStopOnTheFirstRow()
    {
        _preferences.GroupSparkplugTopicsByNodeAndDevice = false;
        _fixture.AddTopics(("spBv1.0/plant/NDATA/gw1", 1));
        var cut = Render<TopicBrowser>();
        EnsureMudProviders();
        TabStopKey(cut).Should().Be("spBv1.0");

        _fixture.AddTopics(("alpha/one", 1));
        await cut.InvokeAsync(() => cut.Instance.RefreshAsync());

        cut.WaitForAssertion(() =>
        {
            FirstRowKey(cut).Should().Be("alpha");
            TabStopKey(cut).Should().Be("alpha");
        });
    }

    [Test]
    public void CheckedBranchOutsideTheTree_DoesNotTakeTheTabStop()
    {
        _preferences.GroupSparkplugTopicsByNodeAndDevice = false;
        _fixture.AddTopics(("sensors/temp", 1), ("sensors/humidity", 1));
        _fixture.SetSelection(TopicSelection.ForBranches(
            [new TopicBranchTarget(TopicBranchKind.Raw, Path: "sensors/humidity")]));

        var cut = Render<TopicBrowser>();
        EnsureMudProviders();
        cut.FindAll(".mud-toolbar button")[0].Click();
        cut.WaitForAssertion(() => cut.FindAll(".topic-tree-row").Should().HaveCount(3));
        cut.Find(".topic-filter-field input").Input("no-match");
        cut.WaitForAssertion(() => cut.FindAll(".topic-tree-row").Should().BeEmpty());
        cut.Find(".topic-filter-field input").Input(string.Empty);
        cut.WaitForAssertion(() => cut.FindAll(".topic-tree-row").Should().HaveCount(3));

        cut.Find(".topic-tree-row[data-topic-key='sensors/humidity']").GetAttribute("aria-selected")
            .Should().Be("true");
        TabStopKey(cut).Should().Be("sensors");
    }

    [Test]
    public async Task GroupedTree_ArrowRightExpandsTheNamespaceRootThenMovesToItsGroup()
    {
        _preferences.GroupSparkplugTopicsByNodeAndDevice = true;
        _fixture.AddTopics(("spBv1.0/plant/NDATA/gw1", 1));
        var cut = Render<TopicBrowser>();
        EnsureMudProviders();
        cut.FindAll(".topic-tree-row").Should().ContainSingle();

        await PressKeyAsync(cut, "ArrowRight");
        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".topic-tree-row").Should().HaveCountGreaterThan(1);
            cut.Find(".topic-tree-row[data-topic-key='spBv1.0']").GetAttribute("aria-expanded")
                .Should().Be("true");
            TabStopKey(cut).Should().Be("spBv1.0");
        });

        await PressKeyAsync(cut, "ArrowRight");
        TabStopKey(cut).Should().Be("spBv1.0/plant");
    }

    [Test]
    public void RowCheckbox_StaysOutOfTheTabOrder()
    {
        _preferences.GroupSparkplugTopicsByNodeAndDevice = true;
        _fixture.AddTopics(("spBv1.0/plant/NDATA/gw1", 1));
        var cut = Render<TopicBrowser>();
        EnsureMudProviders();

        cut.FindAll(".topic-tree-row input[type='checkbox']").Should().ContainSingle()
            .Which.GetAttribute("tabindex").Should().Be("-1");
        cut.FindAll(".topic-tree-row[tabindex='0']").Should().ContainSingle()
            .Which.GetAttribute("data-topic-key").Should().Be("spBv1.0");
    }

    private static Task PressKeyAsync(IRenderedComponent<TopicBrowser> cut, string key) =>
        cut.InvokeAsync(() => cut.Find(".topic-tree-row[tabindex='0']").KeyDown(key));

    private static string? FirstRowKey(IRenderedComponent<TopicBrowser> cut) =>
        cut.FindAll(".topic-tree-row")[0].GetAttribute("data-topic-key");

    private static string? TabStopKey(IRenderedComponent<TopicBrowser> cut)
    {
        var tabStops = cut.FindAll(".topic-tree-row[tabindex='0']");
        tabStops.Should().ContainSingle("a tree keeps exactly one roving tab stop");
        return tabStops[0].GetAttribute("data-topic-key");
    }
}
