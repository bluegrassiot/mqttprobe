using AngleSharp.Dom;
using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.UI.Components.Layout;
using MqttProbe.UI.Tests.TestHelpers;

namespace MqttProbe.UI.Tests.Components.Browser;

// The icon key is the one place that explains both tree modes, so it has to name every glyph the
// tree can show and keep matching the rows it describes.
[TestFixture]
public sealed class TopicBrowserIconKeyTests : BunitTestContext
{
    private static readonly string[] _keyHooks =
    [
        "branch", "leaf", "counts",
        "namespace", "group", "node", "device", "message-type", "grouping-row"
    ];

    private IMessageStoreManager _mockMsgStore = null!;
    private TopicBrowserTestFixture _fixture = null!;
    private readonly UiPreferences _preferences = new();

    [SetUp]
    public void SetupMocks()
    {
        _mockMsgStore = Substitute.For<IMessageStoreManager>();
        _fixture = new TopicBrowserTestFixture(_mockMsgStore);
        _fixture.Configure();
        Services.AddSingleton(_mockMsgStore);
        Services.AddUiSettings(SettingsSubstituteRegistration.CreateUiSettings(_preferences));
    }

    private static TopicNodeSnapshot[] PlainTree() => [TopicBrowserTestTrees.BuildTree()];

    private static TopicNodeSnapshot[] SparkplugTree() =>
    [
        .. TopicSnapshotBuilder.Build(
            ("spBv1.0/plant1/NDATA/node1", 3),
            ("spBv1.0/plant1/NDATA/node1/extra", 1),
            ("spBv1.0/plant1/NBIRTH/node1", 1),
            ("spBv1.0/plant1/DDATA/node1/dev1", 2),
            ("spBv1.0/STATE/scada", 1),
            ("sensors/temp", 4))
    ];

    private IRenderedComponent<TopicBrowser> RenderTopics(
        TopicNodeSnapshot[] tree,
        bool grouped = false,
        bool expandAll = true)
    {
        _preferences.GroupSparkplugTopicsByNodeAndDevice = grouped;
        _fixture.SetSnapshot(tree);
        var cut = Render<TopicBrowser>();
        EnsureMudProviders();
        if (expandAll)
            cut.FindAll(".mud-toolbar button")[0].Click();

        return cut;
    }

    private static void OpenIconKey(IRenderedComponent<TopicBrowser> cut) =>
        cut.Find(".topic-icon-key-toggle").Click();

    private static IElement RowWithLabel(IRenderedComponent<TopicBrowser> cut, string label) =>
        cut.FindAll(".topic-tree-row")
            .Single(row => row.QuerySelector(".flex-grow-1")!.TextContent.Trim() == label);

    private static string RowGlyph(IRenderedComponent<TopicBrowser> cut, string label) =>
        TopicIconShape.Of(RowWithLabel(cut, label).QuerySelector(".topic-node-icon")!);

    private static string KeyGlyph(IRenderedComponent<TopicBrowser> cut, string hook, int index = 0) =>
        TopicIconShape.Of(cut.FindAll($"[data-icon-key='{hook}'] .topic-node-icon")[index]);

    [Test]
    public void IconKeyStaysClosedUntilItsControlIsUsed()
    {
        var cut = RenderTopics(PlainTree());

        cut.FindAll("#topic-icon-key").Should().BeEmpty();

        var toggle = cut.Find(".topic-icon-key-toggle");
        toggle.GetAttribute("aria-expanded").Should().Be("false");
        toggle.GetAttribute("aria-label").Should().Be("Icon key");
        toggle.GetAttribute("aria-controls").Should().BeNull("the key it controls is not on the page");

        OpenIconKey(cut);

        cut.FindAll("#topic-icon-key").Should().ContainSingle();
        cut.Find(".topic-icon-key-toggle").GetAttribute("aria-expanded").Should().Be("true");
        cut.Find(".topic-icon-key-toggle").GetAttribute("aria-controls").Should().Be("topic-icon-key");
    }

    [Test]
    public void IconKeyExplainsBothTreeModesInWords()
    {
        var cut = RenderTopics(PlainTree());
        OpenIconKey(cut);

        cut.Markup.Should().Contain("Topic rows");
        cut.Markup.Should().Contain("Sparkplug grouping");

        foreach (var hook in _keyHooks)
            cut.FindAll($"[data-icon-key='{hook}']").Should().ContainSingle("the key must cover {0}", hook);

        cut.Markup.Should().Contain("Topic with subtopics (folder)");
        cut.Markup.Should().Contain("Topic with no subtopics (green circle, not health or message presence)");
        cut.Markup.Should().Contain("Topics below; messages there and below");
        cut.Markup.Should().Contain("Sparkplug namespace (spBv1.0)");
        cut.Markup.Should().Contain("Grouping row: not a real MQTT topic");
        cut.FindAll("[data-icon-key='direct']").Should().BeEmpty("the separate presence marker is removed");
    }

    [Test]
    public void IconKeyNamesEverySparkplugRoleTheGroupingViewCanShow()
    {
        var cut = RenderTopics(SparkplugTree(), grouped: true);
        OpenIconKey(cut);

        cut.Markup.Should().Contain("Sparkplug group id");
        cut.Markup.Should().Contain("Node or gateway that published");
        cut.Markup.Should().Contain("Device under that node");
        cut.Markup.Should().Contain("Message type, such as NDATA or NCMD: violet under a node, amber under a device");
    }

    [Test]
    public void IconKeySparkplugGlyphsMatchTheRowsTheyDescribe()
    {
        var cut = RenderTopics(SparkplugTree(), grouped: true);
        OpenIconKey(cut);

        KeyGlyph(cut, "namespace").Should().Be(RowGlyph(cut, "spBv1.0"));
        KeyGlyph(cut, "group").Should().Be(RowGlyph(cut, "plant1"));
        KeyGlyph(cut, "node").Should().Be(RowGlyph(cut, "node1"));
        KeyGlyph(cut, "node").Should().Be(TopicIconShape.OfConstant(LucideIcons.Router),
            "the key shows the router glyph for a node or gateway");
        KeyGlyph(cut, "device").Should().Be(RowGlyph(cut, "dev1"));
        KeyGlyph(cut, "message-type").Should().Be(RowGlyph(cut, "NDATA"));
        KeyGlyph(cut, "message-type", index: 1).Should().Be(RowGlyph(cut, "DDATA"));
    }

    [Test]
    public void IconKeyGivesEachOwnerItsOwnRoleClass()
    {
        var cut = RenderTopics(SparkplugTree(), grouped: true);
        OpenIconKey(cut);

        var roles = new[] { "namespace", "group", "node", "device" }
            .Select(hook => cut.Find($"[data-icon-key='{hook}'] .topic-node-icon").ClassList
                .First(c => c.StartsWith("topic-role--", StringComparison.Ordinal)))
            .ToList();

        roles.Should().OnlyHaveUniqueItems("every exposed hierarchy category has its own hue");

        var messageTypeGlyphs = cut.FindAll("[data-icon-key='message-type'] .topic-node-icon");
        messageTypeGlyphs.Should().HaveCount(2,
            "the key shows a message type in both owner tones rather than a hue of its own");
        messageTypeGlyphs[0].ClassList.Should().Contain("topic-role--node");
        messageTypeGlyphs[1].ClassList.Should().Contain("topic-role--device");
        cut.Markup.Should().NotContain("topic-role--message-type", "the purple message-type hue is retired");
    }

    [Test]
    public void IconKeyExplainsTheGreenCircleWithoutClaimingHealthOrPresence()
    {
        var cut = RenderTopics(PlainTree());
        OpenIconKey(cut);

        var leaf = cut.Find("[data-icon-key='leaf'] .topic-node-icon");
        leaf.ClassList.Should().Contain("topic-role--leaf");
        cut.Find("[data-icon-key='leaf']").TextContent.Should()
            .Contain("green circle").And.Contain("not health or message presence");
        cut.FindAll(".topic-icon-key-dot").Should().BeEmpty("the key no longer shows a dot swatch");
    }

    [Test]
    public void TreeWrapCarriesTheDarkRoleTokensByDefault()
    {
        var cut = RenderTopics(PlainTree());

        cut.Find(".topic-tree-wrap").ClassList.Should().Contain("topic-tree--dark");
    }

    [Test]
    public void TreeWrapCarriesTheLightRoleTokensInLightTheme()
    {
        _fixture.SetSnapshot(PlainTree());
        var cut = Render<TopicBrowser>(parameters =>
            parameters.AddCascadingValue("IsDarkMode", false));
        EnsureMudProviders();

        cut.Find(".topic-tree-wrap").ClassList.Should().Contain("topic-tree--light");
        cut.Find(".topic-tree-wrap").ClassList.Should().NotContain("topic-tree--dark");
    }

    [Test]
    public void IconKeyBranchAndLeafGlyphsMatchTheRowsTheyDescribe()
    {
        var cut = RenderTopics(PlainTree(), expandAll: false);
        OpenIconKey(cut);

        KeyGlyph(cut, "branch").Should().Be(RowGlyph(cut, "sensors"));
        KeyGlyph(cut, "branch").Should().Be(TopicIconShape.OfConstant(LucideIcons.Folder));

        cut.Find(".topic-tree-row button").Click();

        KeyGlyph(cut, "branch", index: 1).Should().Be(RowGlyph(cut, "sensors"));
        KeyGlyph(cut, "branch", index: 1).Should().Be(TopicIconShape.OfConstant(LucideIcons.FolderOpen));
        KeyGlyph(cut, "leaf").Should().Be(RowGlyph(cut, "humidity"));
        KeyGlyph(cut, "leaf").Should().Be(TopicIconShape.OfConstant(LucideIcons.Circle));
    }

    [Test]
    public void IconKeyClosesFromItsOwnControl()
    {
        var cut = RenderTopics(PlainTree());
        OpenIconKey(cut);

        var close = cut.Find("#topic-icon-key .mud-icon-button");
        close.GetAttribute("aria-label").Should().Be("Close icon key");

        close.Click();

        cut.FindAll("#topic-icon-key").Should().BeEmpty();
        cut.Find(".topic-icon-key-toggle").GetAttribute("aria-expanded").Should().Be("false");
    }

    [Test]
    public void ToolbarExpandAndCollapseControlsCarryAccessibleLabels()
    {
        var cut = RenderTopics(PlainTree(), expandAll: false);

        var buttons = cut.FindAll(".mud-toolbar button");
        buttons[0].GetAttribute("aria-label").Should().Be("Expand all");
        buttons[1].GetAttribute("aria-label").Should().Be("Collapse all");
        buttons[0].GetAttribute("title").Should().Be("Expand all");
        buttons[1].GetAttribute("title").Should().Be("Collapse all");
    }

    [Test]
    public void OpeningAndClosingTheIconKeyLeavesExpansionAndSelectionAlone()
    {
        var cut = RenderTopics(PlainTree(), expandAll: true);

        var rowsBefore = cut.FindAll(".topic-tree-row").Count;
        rowsBefore.Should().BeGreaterThan(1);

        cut.FindAll(".topic-tree-row")[1].Click();
        cut.FindAll(".topic-tree-row--selected").Should().ContainSingle();

        OpenIconKey(cut);
        AssertTreeUnchanged();

        OpenIconKey(cut);
        AssertTreeUnchanged();

        void AssertTreeUnchanged()
        {
            cut.FindAll(".topic-tree-row").Should().HaveCount(rowsBefore);
            cut.FindAll(".topic-tree-row--selected").Should().ContainSingle();
        }
    }
}
