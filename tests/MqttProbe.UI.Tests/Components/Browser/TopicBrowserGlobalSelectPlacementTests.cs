using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.UI.Tests.TestHelpers;

namespace MqttProbe.UI.Tests.Components.Browser;

// The global branch control belongs to the rows, not the panel title: it sits in its own strip
// directly above the first row, outside the tree's scroll owner, with the legend beside it.
[TestFixture]
public sealed class TopicBrowserGlobalSelectPlacementTests : BunitTestContext
{
    private IMessageStoreManager _mockMsgStore = null!;
    private TopicBrowserTestFixture _fixture = null!;

    [SetUp]
    public void SetupMocks()
    {
        _mockMsgStore = Substitute.For<IMessageStoreManager>();
        _fixture = new TopicBrowserTestFixture(_mockMsgStore);
        Services.AddSingleton<ITopicSelectionStore>(_fixture.Adapter);
        Services.AddUiSettings(SettingsSubstituteRegistration.CreateUiSettings());
    }

    [Test]
    public void GlobalControl_SitsBelowTheToolbarAndAboveTheFirstRow()
    {
        var cut = RenderTopics();

        cut.FindAll(".topic-toolbar, .topic-select-strip, .topic-tree-wrap")
            .Select(element => element.ClassList.Contains("topic-toolbar") ? "toolbar"
                : element.ClassList.Contains("topic-select-strip") ? "strip" : "tree")
            .Should().Equal("toolbar", "strip", "tree");

        cut.Find(".topic-header-row").QuerySelector(".topic-select-all")
            .Should().BeNull("the global control no longer belongs to the panel title");
        cut.Find(".topic-tree-wrap").QuerySelector(".topic-select-all")
            .Should().BeNull("the global control must not scroll away with the rows");
    }

    [Test]
    public void GlobalControl_ShowsTheShortLabelAndTheLegend()
    {
        var cut = RenderTopics();

        cut.Find(".topic-select-all__label").TextContent.Trim().Should().Be("Select all");
        cut.Find(".topic-stat-legend").TextContent.Trim().Should().Be("T = topics · M = messages");
        cut.Find(".topic-select-all input").GetAttribute("aria-label").Should().Be("Select all topics");
    }

    [Test]
    public void GlobalControl_StaysASeparateTabStop()
    {
        var cut = RenderTopics();

        cut.Find(".topic-select-all input").GetAttribute("tabindex").Should().NotBe("-1");
        cut.FindAll(".topic-tree-row input[type='checkbox']").Should().OnlyContain(
            input => input.GetAttribute("tabindex") == "-1");
        cut.FindAll(".topic-tree-row[tabindex='0']").Should().ContainSingle();
    }

    [Test]
    public void PanelTitle_KeepsTheTitleCountAndIconKeyToggle()
    {
        var cut = RenderTopics();

        var header = cut.Find(".topic-header-row");
        header.TextContent.Should().Contain("Topics");
        header.QuerySelector(".topic-count-chip").Should().NotBeNull();
        header.QuerySelector("button[aria-label='Icon key']").Should().NotBeNull();
    }

    private IRenderedComponent<TopicBrowser> RenderTopics()
    {
        _fixture.AddTopics(TopicBrowserTestTrees.BuildTopics());
        var cut = Render<TopicBrowser>();
        EnsureMudProviders();
        return cut;
    }
}
