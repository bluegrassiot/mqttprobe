using System.Globalization;
using AngleSharp.Dom;
using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.UI.Tests.TestHelpers;

namespace MqttProbe.UI.Tests.Components.Browser;

// A row draws its whole indent with one SVG, so its element count must not grow with depth: the
// old spacer-per-level markup cost thousands of nodes per row on a deep topic. The indent itself
// is bounded so a 360px pane never needs a horizontal scrollbar.
[TestFixture]
public class TopicTreeRowIndentRenderingTests : BunitTestContext
{
    private static TopicTreeRowModel Row(int depth, TopicTreeRowKind kind = TopicTreeRowKind.Devices) =>
        new($"spBv1.0/plant1/{depth}", $"level-{depth}", depth, HasChildren: true,
            TopicCount: 3, MessageCount: 42, IsExpanded: true, IsValueBearer: false, Kind: kind);

    private IRenderedComponent<TopicTreeRow> RenderRow(
        TopicTreeRowModel row,
        Action<string>? onExpandToggled = null,
        Action<TopicTreeRowModel>? onActivated = null)
    {
        Action<string> toggled = onExpandToggled ?? (_ => { });
        Action<TopicTreeRowModel> activated = onActivated ?? (_ => { });

        return Render<TopicTreeRow>(parameters => parameters
            .Add(component => component.Row, row)
            .Add(component => component.OnExpandToggled, toggled)
            .Add(component => component.OnActivated, activated));
    }

    private static int ElementsIn(IRenderedComponent<TopicTreeRow> cut) =>
        Descendants(cut.Find(".topic-tree-row"));

    private static int Descendants(IElement element) =>
        element.ChildElementCount + element.Children.Sum(Descendants);

    [Test]
    public void RowElementCountDoesNotGrowWithDepth()
    {
        var shallow = ElementsIn(RenderRow(Row(0)));

        foreach (var depth in new[] { 1, 7, 250, 1000, 4000 })
        {
            ElementsIn(RenderRow(Row(depth)))
                .Should().Be(shallow, "a row at depth {0} must cost the same DOM as a row at depth 0", depth);
        }
    }

    [TestCase(0, 0)]
    [TestCase(1, 16)]
    [TestCase(6, 96)]
    [TestCase(7, 96)]
    [TestCase(12, 96)]
    [TestCase(1000, 96)]
    [TestCase(4096, 96)]
    public void IndentIsBoundedSoNarrowPanesNeverScrollSideways(int depth, int expectedWidth)
    {
        var cut = RenderRow(Row(depth));

        var indent = cut.FindAll(".topic-tree-indent");
        indent.Should().ContainSingle();
        indent[0].GetAttribute("width").Should().Be(expectedWidth.ToString(CultureInfo.InvariantCulture));
        indent[0].GetAttribute("aria-hidden").Should().Be("true");
        cut.Find(".topic-tree-row").GetAttribute("data-depth").Should().Be(depth.ToString(CultureInfo.InvariantCulture));
    }

    [TestCase(0)]
    [TestCase(1000)]
    public void IndentUsesNoInlineStyleOrDepthKeyedClass(int depth)
    {
        var cut = RenderRow(Row(depth));

        cut.Markup.Should().NotContain("style=");
        cut.Markup.Should().NotContain("var(--");
        cut.Find(".topic-tree-indent").ClassName.Should().Be("topic-tree-indent");
    }

    [Test]
    public void DeepGroupingRowClickSelectsTheTopicsItStandsForWithoutTogglingExpand()
    {
        var row = Row(1000, TopicTreeRowKind.Devices) with
        {
            TopicCount = 2,
            Branch = new TopicBranchTarget(
                TopicBranchKind.Sparkplug,
                Scope: new SparkplugSelectionScope(SparkplugSelectionKind.Devices, "spBv1.0/plant1", "node1"))
        };
        string? toggled = null;
        TopicTreeRowModel? activated = null;
        var cut = RenderRow(row, path => toggled = path, model => activated = model);

        cut.Find(".topic-tree-row").Click();

        activated.Should().BeSameAs(row);
        row.ToSelection().Branches.Should().ContainSingle().Which.Should().Be(row.Branch);
        toggled.Should().BeNull("expansion belongs to the chevron");
    }

    [Test]
    public void DeepRealRowClickSelectsTheTopic()
    {
        var row = Row(1000, TopicTreeRowKind.Topic) with
        {
            Branch = new TopicBranchTarget(
                TopicBranchKind.Raw, Path: "spBv1.0/plant1/1000")
        };
        TopicTreeRowModel? activated = null;
        var cut = RenderRow(row, null, model => activated = model);

        cut.Find(".topic-tree-row").Click();

        activated.Should().BeSameAs(row);
        row.ToSelection().Key.Should().Be("spBv1.0/plant1/1000");
        row.ToSelection().Topics.Should().Equal("spBv1.0/plant1/1000");
    }

    [Test]
    public void DeepRowKeepsItsLabelCaptionAndTooltips()
    {
        var cut = RenderRow(Row(1000));

        cut.Find(".topic-tree-row").TextContent.Should().Contain("level-1000");
        cut.Find(".topic-tree-row").TextContent.Should().Contain("T 3 · M 42");
        cut.Find(".topic-tree-row .flex-grow-1").GetAttribute("title").Should().Be("level-1000");
        cut.Find(".topic-stat-text").GetAttribute("title")
            .Should().Be("3 topics below, 42 messages in the topics below it");
    }

    [Test]
    public void DeepGroupingRowKeepsItsGroupingTitleAndGroupClass()
    {
        var cut = RenderRow(Row(1000, TopicTreeRowKind.Devices) with
        {
            TopicCount = 2,
        });

        cut.Find(".topic-tree-row").GetAttribute("title")
            .Should().Be("Sparkplug devices · grouping row, not a real MQTT topic · selects 2 topics beneath it");
        cut.Find(".topic-tree-row").ClassName.Should().Contain("topic-tree-row--group");
        cut.Find(".topic-group-label").TextContent.Should().Be("level-1000");
    }

    [Test]
    public void PlainTopicRowCarriesNoRoleTooltip()
    {
        var cut = RenderRow(Row(1000, TopicTreeRowKind.Topic));

        cut.Find(".topic-tree-row").HasAttribute("title").Should().BeFalse(
            "an ordinary topic row has no Sparkplug role to state");
    }

    [Test]
    public void LeafRowKeepsTheChevronSpacerAtDepth()
    {
        var cut = RenderRow(Row(1000) with { HasChildren = false });

        cut.FindAll(".topic-tree-chevron-spacer").Should().ContainSingle();
        cut.FindAll(".topic-tree-row button").Should().BeEmpty();
    }
}
