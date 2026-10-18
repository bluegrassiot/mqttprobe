using AngleSharp.Dom;
using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.UI.Components.Layout;
using MqttProbe.UI.Tests.TestHelpers;

namespace MqttProbe.UI.Tests.Components.Browser;

// One icon language for the whole tree: shape says structure (folder) or endpoint (circle), and
// colour says whose scope a row belongs to: a node shares its tone with its message types and a
// device with its own, and no marker sits beside the label. A green circle means an ordinary
// topic, not health or message presence. No glyph carries depth.
[TestFixture]
public sealed class TopicTreeRowIconLanguageTests : BunitTestContext
{
    // Every colour MudBlazor can paint an icon with; the tree must use none of them.
    private static readonly string[] _paletteColourClasses =
    [
        "mud-primary-text", "mud-secondary-text", "mud-tertiary-text", "mud-info-text",
        "mud-success-text", "mud-warning-text", "mud-error-text", "mud-dark-text"
    ];

    private IMessageStoreManager _manager = null!;

    [SetUp]
    public void SetupMocks()
    {
        _manager = Substitute.For<IMessageStoreManager>();
        _manager.GetSelectedTopicState().Returns(
            new SelectedTopicState(new SelectedTopicToken(Guid.NewGuid(), 1, 0), null, 0));
        Services.AddSingleton(_manager);
        EnsureMudProviders();
    }

    private static TopicTreeRowModel Row(
        bool hasChildren,
        bool hasDirectMessages,
        bool expanded = true,
        TopicTreeRowKind kind = TopicTreeRowKind.Topic,
        int topicCount = 3,
        int messageCount = 42,
        TopicTreeOwner? owner = null,
        string label = "temp") =>
        new("sensors/temp", label, Depth: 2, HasChildren: hasChildren, TopicCount: topicCount,
            MessageCount: messageCount, IsExpanded: expanded, IsValueBearer: hasDirectMessages, Kind: kind,
            Owner: owner);

    private IRenderedComponent<TopicTreeRow> RenderRow(TopicTreeRowModel row) =>
        Render<TopicTreeRow>(parameters => parameters
            .Add(component => component.Row, row)
            .Add(component => component.OnExpandToggled, (string p) => { })
            .Add(component => component.OnSelected, (TopicSelection p) => { }));

    private static IElement NodeGlyph(IRenderedComponent<TopicTreeRow> cut) =>
        cut.Find(".topic-node-icon");

    private static string GlyphShape(IRenderedComponent<TopicTreeRow> cut) =>
        TopicIconShape.Of(NodeGlyph(cut));

    private string RoleOf(TopicTreeRowModel row) =>
        RenderRow(row).Find(".topic-node-icon").ClassList
            .First(className => className.StartsWith("topic-role--", StringComparison.Ordinal));

    private static void AssertNoPaletteColourClass(IElement element, string what)
    {
        foreach (var colourClass in _paletteColourClasses)
            element.ClassList.Should().NotContain(colourClass, "{0} must be neutral", what);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void BranchKeepsTheFolderGlyphWithAndWithoutDirectMessages(bool expanded)
    {
        var plain = RenderRow(Row(hasChildren: true, hasDirectMessages: false, expanded: expanded));
        var populated = RenderRow(Row(hasChildren: true, hasDirectMessages: true, expanded: expanded));
        var folder = expanded ? LucideIcons.FolderOpen : LucideIcons.Folder;

        GlyphShape(populated).Should().Be(GlyphShape(plain),
            "a branch is structure and stays a folder whether or not it holds messages of its own");
        GlyphShape(plain).Should().Be(TopicIconShape.OfConstant(folder));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void LeafKeepsTheSameGlyphWithAndWithoutDirectMessages(bool hasDirectMessages)
    {
        var cut = RenderRow(Row(hasChildren: false, hasDirectMessages: hasDirectMessages));
        var other = RenderRow(Row(hasChildren: false, hasDirectMessages: !hasDirectMessages));

        GlyphShape(cut).Should().Be(GlyphShape(other),
            "the message indicator is separate, so the leaf glyph never swaps for it");
        GlyphShape(cut).Should().Be(TopicIconShape.OfConstant(LucideIcons.Circle));
    }

    [Test]
    public void BranchAndLeafUseDifferentGlyphShapes()
    {
        var branch = RenderRow(Row(hasChildren: true, hasDirectMessages: false));
        var leaf = RenderRow(Row(hasChildren: false, hasDirectMessages: false));

        GlyphShape(branch).Should().NotBe(GlyphShape(leaf),
            "structure and endpoint must be told apart by shape");
    }

    [Test]
    public void CollapsedAndExpandedBranchesUseDifferentFolderGlyphs()
    {
        var collapsed = RenderRow(Row(hasChildren: true, hasDirectMessages: false, expanded: false));
        var expanded = RenderRow(Row(hasChildren: true, hasDirectMessages: false, expanded: true));

        GlyphShape(collapsed).Should().Be(TopicIconShape.OfConstant(LucideIcons.Folder));
        GlyphShape(expanded).Should().Be(TopicIconShape.OfConstant(LucideIcons.FolderOpen));
    }

    [TestCase(false, LucideIcons.Folder)]
    [TestCase(true, LucideIcons.FolderOpen)]
    public void TheDevicesContainerFollowsTheFolderRule(bool expanded, string folderGlyph)
    {
        var cut = RenderRow(Row(hasChildren: true, hasDirectMessages: false, expanded: expanded,
            kind: TopicTreeRowKind.Devices));

        GlyphShape(cut).Should().Be(TopicIconShape.OfConstant(folderGlyph));
    }

    [Test]
    public void NoTreeGlyphOrChevronCarriesAPaletteColourClass()
    {
        TopicTreeRowModel[] rows =
        [
            Row(hasChildren: true, hasDirectMessages: false),
            Row(hasChildren: true, hasDirectMessages: true, expanded: false),
            Row(hasChildren: false, hasDirectMessages: true),
            Row(hasChildren: false, hasDirectMessages: false),
            Row(hasChildren: true, hasDirectMessages: false, kind: TopicTreeRowKind.SparkplugNamespace),
            Row(hasChildren: true, hasDirectMessages: false, kind: TopicTreeRowKind.SparkplugGroup),
            Row(hasChildren: true, hasDirectMessages: false, kind: TopicTreeRowKind.SparkplugNode),
            Row(hasChildren: true, hasDirectMessages: false, kind: TopicTreeRowKind.Devices),
            Row(hasChildren: true, hasDirectMessages: false, kind: TopicTreeRowKind.SparkplugDevice),
            Row(hasChildren: false, hasDirectMessages: false, kind: TopicTreeRowKind.MessageType)
        ];

        foreach (var row in rows)
        {
            var cut = RenderRow(row);
            AssertNoPaletteColourClass(NodeGlyph(cut), $"row kind {row.Kind}");
            if (row.HasChildren)
                AssertNoPaletteColourClass(cut.Find(".topic-tree-row button"), $"the chevron of {row.Kind}");
        }
    }

    [Test]
    public void ChevronCarriesTheBrandColourHook()
    {
        var cut = RenderRow(Row(hasChildren: true, hasDirectMessages: false));

        var chevron = cut.Find(".topic-tree-row button");
        chevron.ClassList.Should().Contain("topic-chevron",
            "the tree's own stylesheet paints the chevron with the brand tone");
        chevron.ClassList.Should().Contain("topic-role--brand",
            "orange is the brand and chevron role, not selection only");
        chevron.ClassList.Should().NotContain("mud-primary-text");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void BranchKeepsTheBlueBranchRoleWithAndWithoutDirectMessages(bool hasDirectMessages)
    {
        var cut = RenderRow(Row(hasChildren: true, hasDirectMessages: hasDirectMessages));

        NodeGlyph(cut).ClassList.Should().Contain("topic-role--branch");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void LeafKeepsTheGreenLeafRoleWithAndWithoutMessages(bool hasDirectMessages)
    {
        var cut = RenderRow(Row(hasChildren: false, hasDirectMessages: hasDirectMessages));

        NodeGlyph(cut).ClassList.Should().Contain("topic-role--leaf",
            "an ordinary topic is a green circle whether or not it holds messages");
    }

    [TestCase(TopicTreeRowKind.SparkplugNamespace, "namespace")]
    [TestCase(TopicTreeRowKind.SparkplugGroup, "group")]
    [TestCase(TopicTreeRowKind.SparkplugNode, "node")]
    [TestCase(TopicTreeRowKind.SparkplugDevice, "device")]
    [TestCase(TopicTreeRowKind.Devices, "device")]
    public void SparkplugCategoriesCarryTheirOwnRole(TopicTreeRowKind kind, string role)
    {
        var cut = RenderRow(Row(hasChildren: true, hasDirectMessages: false, kind: kind));

        NodeGlyph(cut).ClassList.Should().Contain($"topic-role--{role}",
            "each exposed hierarchy category has its own hue; the Devices folder shares the device hue");
    }

    [Test]
    public void NodeAndItsMessageTypesShareOneRoleAndDevicesAnother()
    {
        var node = RoleOf(Row(hasChildren: true, hasDirectMessages: false, kind: TopicTreeRowKind.SparkplugNode));
        var nodeType = RoleOf(Row(hasChildren: false, hasDirectMessages: false,
            kind: TopicTreeRowKind.MessageType, owner: TopicTreeOwner.Node));
        var device = RoleOf(Row(hasChildren: true, hasDirectMessages: false, kind: TopicTreeRowKind.SparkplugDevice));
        var deviceType = RoleOf(Row(hasChildren: false, hasDirectMessages: false,
            kind: TopicTreeRowKind.MessageType, owner: TopicTreeOwner.Device));

        nodeType.Should().Be(node, "a node's message types take the node's colour");
        deviceType.Should().Be(device, "a device's message types take the device's colour");
        node.Should().NotBe(device, "a node and a device never collapse onto one hue");
    }

    [TestCase("NBIRTH", TopicTreeOwner.Node, "node")]
    [TestCase("NDATA", TopicTreeOwner.Node, "node")]
    [TestCase("NCMD", TopicTreeOwner.Node, "node")]
    [TestCase("DBIRTH", TopicTreeOwner.Device, "device")]
    [TestCase("DDATA", TopicTreeOwner.Device, "device")]
    [TestCase("DCMD", TopicTreeOwner.Device, "device")]
    public void MessageTypeTakesItsOwnersColour(string label, TopicTreeOwner owner, string role)
    {
        var row = Row(hasChildren: false, hasDirectMessages: false,
            kind: TopicTreeRowKind.MessageType, owner: owner, label: label);
        var cut = RenderRow(row);

        NodeGlyph(cut).ClassList.Should().Contain($"topic-role--{role}",
            $"{label} belongs to its {owner} owner and shares that owner's colour");
    }

    [Test]
    public void ARoleLooksTheSameAtEveryDepth()
    {
        var classes = new[] { 0, 1, 7, 1000 }
            .Select(depth => string.Join(" ", RenderRow(
                Row(hasChildren: true, hasDirectMessages: true) with { Depth = depth })
                .Find(".topic-node-icon").ClassList))
            .ToList();

        classes.Distinct().Should().ContainSingle("colour follows the role, never the depth");
    }

    [TestCase(true, true)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(false, false)]
    public void NoDirectMessageMarkerIsRenderedOnAnyRow(bool hasChildren, bool isValueBearer)
    {
        var cut = RenderRow(Row(hasChildren: hasChildren, hasDirectMessages: isValueBearer));

        cut.FindAll(".topic-direct-badge").Should().BeEmpty(
            "the separate message-presence marker is removed, so no row reserves space for it");
        cut.Markup.Should().NotContain("Has messages published directly on this topic");
    }

    [Test]
    public void PopulatedAndEmptyLeavesShareTheSameGreenCircle()
    {
        var populated = RenderRow(Row(hasChildren: false, hasDirectMessages: true));
        var empty = RenderRow(Row(hasChildren: false, hasDirectMessages: false));

        GlyphShape(populated).Should().Be(TopicIconShape.OfConstant(LucideIcons.Circle));
        GlyphShape(empty).Should().Be(GlyphShape(populated));
        populated.Find(".topic-node-icon").ClassList.Should().Equal(empty.Find(".topic-node-icon").ClassList);
    }

    [Test]
    public void DatabaseGlyphIsRetiredFromTheTree()
    {
        var cut = RenderRow(Row(hasChildren: false, hasDirectMessages: true));

        cut.Markup.Should().NotContain(LucideIcons.Database,
            "the database metaphor no longer stands for a topic that holds messages");
    }


    [TestCase(3, 42, "3 topics below, 42 messages in this topic and below")]
    [TestCase(1, 1, "1 topic below, 1 message in this topic and below")]
    [TestCase(0, 0, "no topics below, no messages in this topic and below")]
    public void StatsTooltipStatesTheSubtreeTotal(int topicCount, int messageCount, string expected)
    {
        var cut = RenderRow(Row(hasChildren: false, hasDirectMessages: false,
            topicCount: topicCount, messageCount: messageCount));

        cut.Find(".topic-stat-text").GetAttribute("title").Should().Be(expected);
    }

    [Test]
    public void VirtualRowStatsTooltipCountsTheTopicsBelowIt()
    {
        var cut = RenderRow(Row(hasChildren: true, hasDirectMessages: false,
            kind: TopicTreeRowKind.Devices, topicCount: 2, messageCount: 7));

        cut.Find(".topic-stat-text").GetAttribute("title")
            .Should().Be("2 topics below, 7 messages in the topics below it");
    }

    [TestCase(TopicTreeRowKind.SparkplugNode, "Sparkplug node")]
    [TestCase(TopicTreeRowKind.Devices, "Sparkplug devices")]
    [TestCase(TopicTreeRowKind.SparkplugDevice, "Sparkplug device")]
    [TestCase(TopicTreeRowKind.MessageType, "Sparkplug message type")]
    public void VirtualRowTitleNamesTheRoleAndSaysItIsNotARealTopic(TopicTreeRowKind kind, string role)
    {
        var cut = RenderRow(Row(hasChildren: true, hasDirectMessages: false, kind: kind) with
        {
            SelectionTopics = ["spBv1.0/plant1/node1/dev1", "spBv1.0/plant1/node1/dev2"],
        });

        cut.Find(".topic-tree-row").GetAttribute("title").Should().Be(
            $"{role} · grouping row, not a real MQTT topic · selects 2 topics beneath it");
    }

    [Test]
    public void VirtualRowTitleCountsOneSelectedTopicInTheSingular()
    {
        var cut = RenderRow(Row(hasChildren: true, hasDirectMessages: false,
            kind: TopicTreeRowKind.SparkplugNode) with
        {
            SelectionTopics = ["spBv1.0/plant1/NDATA/node1"],
        });

        cut.Find(".topic-tree-row").GetAttribute("title").Should().Be(
            "Sparkplug node · grouping row, not a real MQTT topic · selects 1 topic beneath it");
    }

    [TestCase(TopicTreeRowKind.SparkplugNamespace, "Sparkplug namespace · real MQTT topic")]
    [TestCase(TopicTreeRowKind.SparkplugGroup, "Sparkplug group · real MQTT topic")]
    public void RealRoleRowTitleNamesTheRoleAndSaysItIsARealTopic(TopicTreeRowKind kind, string expected)
    {
        var cut = RenderRow(Row(hasChildren: true, hasDirectMessages: false, kind: kind));

        cut.Find(".topic-tree-row").GetAttribute("title").Should().Be(expected);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void PlainTopicRowTitleCarriesNoRole(bool hasChildren)
    {
        var cut = RenderRow(Row(hasChildren: hasChildren, hasDirectMessages: false));

        cut.Find(".topic-tree-row").HasAttribute("title").Should().BeFalse(
            "an ordinary topic row has no Sparkplug role to state");
    }

    [Test]
    public void ExpandAndCollapseButtonsCarryAccessibleLabelsAndExpandedState()
    {
        var collapsed = RenderRow(Row(hasChildren: true, hasDirectMessages: false, expanded: false));
        var chevron = collapsed.Find(".topic-tree-row button");

        chevron.GetAttribute("aria-label").Should().Be("Expand temp");
        chevron.GetAttribute("aria-expanded").Should().Be("false");
        chevron.GetAttribute("title").Should().Be("Expand temp");

        var expanded = RenderRow(Row(hasChildren: true, hasDirectMessages: false, expanded: true));
        var opened = expanded.Find(".topic-tree-row button");

        opened.GetAttribute("aria-label").Should().Be("Collapse temp");
        opened.GetAttribute("aria-expanded").Should().Be("true");
    }
}
