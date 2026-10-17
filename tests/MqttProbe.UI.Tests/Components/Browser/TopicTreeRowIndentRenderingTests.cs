using System.Globalization;
using AngleSharp.Dom;
using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.UI.Tests.TestHelpers;

namespace MqttProbe.UI.Tests.Components.Browser;

// A row draws its whole indent with one SVG, so its element count must not grow with depth: the
// old spacer-per-level markup cost thousands of nodes per row on a deep topic.
[TestFixture]
public class TopicTreeRowIndentRenderingTests : BunitTestContext
{
    private IMessageStoreManager _manager = null!;

    [SetUp]
    public void SetupMocks()
    {
        _manager = Substitute.For<IMessageStoreManager>();
        _manager.GetSelectedTopicState().Returns(Selection(null));
        Services.AddSingleton(_manager);
        EnsureMudProviders();
    }

    private static SelectedTopicState Selection(string? topic) =>
        new(new SelectedTopicToken(Guid.NewGuid(), 1, 0), topic, 0);

    private static TopicTreeRowModel Row(int depth, TopicTreeRowKind kind = TopicTreeRowKind.Devices) =>
        new($"spBv1.0/plant1/{depth}", $"level-{depth}", depth, HasChildren: true,
            TopicCount: 3, MessageCount: 42, IsExpanded: true, IsValueBearer: false, Kind: kind);

    private IRenderedComponent<TopicTreeRow> RenderRow(
        TopicTreeRowModel row,
        Action<string>? onExpandToggled = null,
        Action<string>? onSelected = null)
    {
        Action<string> toggled = onExpandToggled ?? (_ => { });
        Action<string> selected = onSelected ?? (_ => { });

        return Render<TopicTreeRow>(parameters => parameters
            .Add(component => component.Row, row)
            .Add(component => component.OnExpandToggled, toggled)
            .Add(component => component.OnSelected, selected));
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
    [TestCase(7, 112)]
    [TestCase(1000, 16000)]
    [TestCase(4096, 65536)]
    public void IndentIsOneElementSizedByDepth(int depth, int expectedWidth)
    {
        var cut = RenderRow(Row(depth));

        var indent = cut.FindAll(".topic-tree-indent");
        indent.Should().ContainSingle();
        indent[0].GetAttribute("width").Should().Be(expectedWidth.ToString(CultureInfo.InvariantCulture));
        indent[0].GetAttribute("aria-hidden").Should().Be("true");
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
            SelectionTopics = ["spBv1.0/plant1/node1/dev1", "spBv1.0/plant1/node1/dev2"],
        };
        var current = Selection(null);
        _manager.GetSelectedTopicState().Returns(_ => current);
        _manager.When(m => m.SelectTopics(Arg.Any<TopicSelection?>()))
            .Do(call => current = Selection(call.Arg<TopicSelection>()?.Key));

        string? toggled = null;
        string? selected = null;
        var cut = RenderRow(row, path => toggled = path, path => selected = path);

        cut.Find(".topic-tree-row").Click();

        _manager.Received(1).SelectTopics(Arg.Is<TopicSelection>(selection =>
            selection.Key == row.FullPath
            && selection.Topics.SequenceEqual(row.SelectionTopics.ToArray())));
        selected.Should().Be(row.FullPath);
        toggled.Should().BeNull("expansion belongs to the chevron");
    }

    [Test]
    public void DeepRealRowClickSelectsTheTopic()
    {
        var row = Row(1000, TopicTreeRowKind.Topic);
        var current = Selection(null);
        _manager.GetSelectedTopicState().Returns(_ => current);
        _manager.When(m => m.SelectTopics(Arg.Any<TopicSelection?>()))
            .Do(call => current = Selection(call.Arg<TopicSelection>()?.Key));

        string? selected = null;
        var cut = RenderRow(row, null, path => selected = path);

        cut.Find(".topic-tree-row").Click();

        _manager.Received(1).SelectTopics(Arg.Is<TopicSelection>(selection =>
            selection.Key == row.FullPath
            && selection.Topics.Length == 1
            && selection.Topics[0] == row.FullPath));
        selected.Should().Be(row.FullPath);
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
            SelectionTopics = ["spBv1.0/plant1/node1/dev1", "spBv1.0/plant1/node1/dev2"],
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
