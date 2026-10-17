using AngleSharp.Dom;
using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.UI.Components.Layout;
using MqttProbe.UI.Tests.TestHelpers;

namespace MqttProbe.UI.Tests.Components.Browser;

// The grouping view keeps its Sparkplug role glyphs and gives each owner one hue: a node shares
// its tone with its message types and a device with its own. Ordinary topics stay green circles
// or blue branches, and no separate message-presence marker exists.
[TestFixture]
public sealed class TopicTreeGroupedIconLanguageTests : BunitTestContext
{
    private const string NodeTip = "spBv1.0/plant1/NDATA/node1";
    private const string NodeCommandTip = "spBv1.0/plant1/NCMD/node1";
    private const string DeviceCommandTip = "spBv1.0/plant1/DCMD/node1/dev1";

    private static readonly string[] _paletteColourClasses =
    [
        "mud-primary-text", "mud-secondary-text", "mud-tertiary-text", "mud-info-text",
        "mud-success-text", "mud-warning-text", "mud-error-text", "mud-dark-text"
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

    private static TopicNodeSnapshot[] GroupedTree(bool withResidueBelowTheNode) =>
    [
        .. TopicSnapshotBuilder.Build(
            withResidueBelowTheNode
                ? new (string Topic, int Messages)[]
                {
                    (NodeTip, 3),
                    ($"{NodeTip}/extra/leaf", 1),
                    ("spBv1.0/plant1/NBIRTH/node1", 1),
                    ("spBv1.0/plant1/DDATA/node1/dev1", 2),
                    ("spBv1.0/STATE/scada", 1)
                }
                :
                new (string Topic, int Messages)[]
                {
                    (NodeTip, 3),
                    ("spBv1.0/plant1/NBIRTH/node1", 1),
                    ("spBv1.0/plant1/DDATA/node1/dev1", 2),
                    ("spBv1.0/STATE/scada", 1)
                })
    ];

    private IRenderedComponent<TopicBrowser> RenderGrouped(TopicNodeSnapshot[] tree)
    {
        _preferences.GroupSparkplugTopicsByNodeAndDevice = true;
        _fixture.SetSnapshot(tree);
        var cut = Render<TopicBrowser>();
        EnsureMudProviders();
        cut.FindAll(".mud-toolbar button")[0].Click();

        return cut;
    }

    private static TopicNodeSnapshot[] CommandTree() =>
    [
        .. TopicSnapshotBuilder.Build(
            (NodeTip, 3),
            ("spBv1.0/plant1/NBIRTH/node1", 1),
            (NodeCommandTip, 1),
            ("spBv1.0/plant1/DDATA/node1/dev1", 1),
            (DeviceCommandTip, 1))
    ];

    private static IElement RowWithLabel(IRenderedComponent<TopicBrowser> cut, string label)
    {
        var rows = cut.FindAll(".topic-tree-row")
            .Where(row => row.QuerySelector(".topic-name")?.TextContent.Trim() == label)
            .ToList();
        var labels = cut.FindAll(".topic-tree-row .topic-name")
            .Select(name => name.TextContent.Trim());
        rows.Should().ContainSingle("the rendered tree should contain {0}; labels: {1}",
            label, string.Join(" | ", labels));
        return rows[0];
    }

    private static IElement RowIcon(IRenderedComponent<TopicBrowser> cut, string label) =>
        RowWithLabel(cut, label).QuerySelector(".topic-node-icon")!;

    [TestCase("spBv1.0", LucideIcons.Zap)]
    [TestCase("plant1", LucideIcons.Layers)]
    [TestCase("node1", LucideIcons.Router)]
    [TestCase("dev1", LucideIcons.Network)]
    [TestCase("NDATA", LucideIcons.Hash)]
    public void SparkplugRoleGlyphsArePreserved(string label, string expectedGlyph)
    {
        var cut = RenderGrouped(GroupedTree(withResidueBelowTheNode: false));

        TopicIconShape.Of(RowIcon(cut, label)).Should().Be(TopicIconShape.OfConstant(expectedGlyph));
    }

    [Test]
    public void NodeGlyphIsTheRouterNotTheProcessor()
    {
        var cut = RenderGrouped(GroupedTree(withResidueBelowTheNode: false));

        var node = TopicIconShape.Of(RowIcon(cut, "node1"));
        node.Should().Be(TopicIconShape.OfConstant(LucideIcons.Router),
            "the node or gateway row uses the router glyph");
        node.Should().NotBe(TopicIconShape.OfConstant(LucideIcons.Cpu),
            "the processor glyph no longer stands for a node or gateway");
    }

    [TestCase("spBv1.0", "namespace")]
    [TestCase("plant1", "group")]
    [TestCase("node1", "node")]
    [TestCase("dev1", "device")]
    [TestCase("NDATA", "node")]
    [TestCase("DDATA", "device")]
    [TestCase("extra", "branch")]
    [TestCase("leaf", "leaf")]
    public void EveryGroupedRowGlyphCarriesItsRoleTone(string label, string role)
    {
        var cut = RenderGrouped(GroupedTree(withResidueBelowTheNode: true));

        var classes = RowIcon(cut, label).ClassList;
        classes.Should().Contain($"topic-role--{role}");
        foreach (var colourClass in _paletteColourClasses)
            classes.Should().NotContain(colourClass, "the row for {0} uses the tree role tokens", label);
    }

    [TestCase("NBIRTH", "node")]
    [TestCase("NDATA", "node")]
    [TestCase("NCMD", "node")]
    [TestCase("DDATA", "device")]
    [TestCase("DCMD", "device")]
    public void MessageTypeTakesItsOwnersColour(string label, string role)
    {
        var cut = RenderGrouped(CommandTree());

        var classes = RowIcon(cut, label).ClassList;
        classes.Should().Contain($"topic-role--{role}",
            "{0} belongs to its owner and shares that owner's colour", label);
        classes.Should().NotContain("topic-role--message-type", "no separate message-type hue remains");
    }

    [Test]
    public void OwnerColourDoesNotReachTheRealTopicBeneathIt()
    {
        var cut = RenderGrouped(CommandTree());

        RowIcon(cut, NodeCommandTip).ClassList.Should().Contain("topic-role--leaf");
        RowIcon(cut, NodeCommandTip).ClassList.Should().NotContain("topic-role--node");
        RowIcon(cut, DeviceCommandTip).ClassList.Should().Contain("topic-role--leaf");
        RowIcon(cut, DeviceCommandTip).ClassList.Should().NotContain("topic-role--device");
    }

    [TestCase("node1")]
    [TestCase("dev1")]
    [TestCase("NDATA")]
    [TestCase("NBIRTH")]
    [TestCase("DDATA")]
    public void NoRowCarriesASeparateMessagePresenceMarker(string label)
    {
        var cut = RenderGrouped(GroupedTree(withResidueBelowTheNode: false));

        RowWithLabel(cut, label).QuerySelector(".topic-direct-badge").Should().BeNull(
            "the separate message-presence marker is removed from both real and grouping rows");
    }

    [TestCase("spBv1.0", "Sparkplug namespace · real MQTT topic")]
    [TestCase("plant1", "Sparkplug group · real MQTT topic")]
    public void RealRoleRowsSayTheyAreRealMqttTopics(string label, string expected)
    {
        var cut = RenderGrouped(GroupedTree(withResidueBelowTheNode: false));

        RowWithLabel(cut, label).GetAttribute("title").Should().Be(expected);
    }

    [Test]
    public void PlainTopicRowCarriesNoRoleTooltip()
    {
        var cut = RenderGrouped(GroupedTree(withResidueBelowTheNode: false));

        RowWithLabel(cut, NodeTip).HasAttribute("title").Should().BeFalse(
            "an ordinary topic row has no Sparkplug role to state");
    }

    [TestCase("node1", "Sparkplug node")]
    [TestCase("dev1", "Sparkplug device")]
    [TestCase("NDATA", "Sparkplug message type")]
    public void VirtualRowsNameTheirRoleAndSayTheyAreNotRealTopics(string label, string role)
    {
        var cut = RenderGrouped(GroupedTree(withResidueBelowTheNode: false));

        var title = RowWithLabel(cut, label).GetAttribute("title");
        title.Should().StartWith($"{role} · grouping row, not a real MQTT topic");
        title.Should().Contain("beneath it", "the tooltip says how much a grouping row selects");
    }

    [Test]
    public void PopulatedRealTopicsShowACircleNotAMarkerAndTheSubtreeTooltipIsUnchanged()
    {
        var cut = RenderGrouped(GroupedTree(withResidueBelowTheNode: true));

        RowWithLabel(cut, NodeTip).QuerySelector(".topic-direct-badge").Should().BeNull();
        RowWithLabel(cut, "leaf").QuerySelector(".topic-direct-badge").Should().BeNull();
        RowIcon(cut, NodeTip).ClassList.Should().Contain("topic-role--branch",
            "the node tip has a topic beneath it, so it is a blue branch");

        var extra = RowWithLabel(cut, "extra");
        extra.QuerySelector(".topic-direct-badge").Should().BeNull();
        extra.QuerySelector(".topic-stat-text")!.GetAttribute("title")
            .Should().Be("1 topic below, 1 message in this topic and below",
                "the subtree total is stated without claiming a message of the row's own");
    }
}
