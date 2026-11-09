using AngleSharp.Dom;
using Microsoft.AspNetCore.Components;
using MqttProbe.Core.Models.Sparkplug;
using MqttProbe.UI.Tests.TestHelpers;

namespace MqttProbe.UI.Tests.Components.Sparkplug;

[TestFixture]
public sealed class SparkplugMetricsTableHierarchyTests : BunitTestContext
{
    private const string Topic = "spBv1.0/factory/DDATA/edge-01/sensor-A";

    [SetUp]
    public void Setup() => EnsureMudProviders();

    [Test]
    public void HierarchyMode_ExposesNarrowLayoutHooks()
    {
        var cut = RenderTable(SparkplugMetricViewMode.Hierarchy, Metric("A/T1"));

        cut.FindAll("th.spb-th-type").Should().ContainSingle();
        cut.FindAll("th.spb-th-alias").Should().ContainSingle();
        cut.FindAll("th.spb-th-updated").Should().ContainSingle();
        cut.FindAll("td.spb-td-type").Should().NotBeEmpty();
        cut.FindAll("td.spb-td-alias").Should().NotBeEmpty();
        cut.FindAll("td.spb-td-updated").Should().NotBeEmpty();
        cut.FindAll("col.spb-col-type").Should().ContainSingle();
        cut.FindAll("col.spb-col-alias").Should().ContainSingle();
        cut.FindAll("col.spb-col-updated").Should().ContainSingle();
        cut.FindAll(".spb-metric-meta").Should().NotBeEmpty();
    }

    [Test]
    public void TableContainer_DoesNotRequestHorizontalScrolling()
    {
        var cut = RenderTable(SparkplugMetricViewMode.Hierarchy, Metric("A/T1"));

        var style = cut.Find(".mud-table-container").GetAttribute("style");
        style.Should().NotContain("overflow-x: auto");
    }

    [TestCase("Deep/One/Two/Three/Four/Five/Six/Seven", "Seven")]
    [TestCase("A//B", "B")]
    [TestCase("/Leading/Metric", "Metric")]
    [TestCase("A/B/", "(empty)")]
    public async Task ModeSwitch_UpdatesVisibleLabelAndKeepsExactChartIdentity(
        string fullPath, string hierarchyLabel)
    {
        SparkplugChartSelection? captured = null;
        var cut = Render<SparkplugMetricsTable>(parameters => parameters
            .Add(component => component.Items, [Metric(fullPath, "23.4")])
            .Add(component => component.Topic, Topic)
            .Add(component => component.Mode, SparkplugMetricViewMode.Hierarchy)
            .Add(component => component.OnAddToChart,
                EventCallback.Factory.Create<SparkplugChartSelection>(this, selection => captured = selection)));

        VisibleLabel(cut, fullPath).Should().Be(hierarchyLabel);

        cut.Render(parameters => parameters.Add(component => component.Mode, SparkplugMetricViewMode.Flat));
        VisibleLabel(cut, fullPath).Should().Be(fullPath);

        cut.Render(parameters => parameters.Add(component => component.Mode, SparkplugMetricViewMode.Hierarchy));
        VisibleLabel(cut, fullPath).Should().Be(hierarchyLabel);

        await cut.InvokeAsync(() => Row(cut, fullPath)
            .QuerySelector("button[title='Add to chart']")!.Click());

        captured.Should().NotBeNull();
        captured!.JsonPath.Should().Be($"metrics.{fullPath}");
        captured.SeriesName.Should().Be(fullPath);
    }

    [Test]
    public void HierarchyMode_CollapsesBranchFoldersUntilExpanded()
    {
        var cut = RenderTable(SparkplugMetricViewMode.Hierarchy, Metric("A/T1"), Metric("A/T2"));

        var folder = Row(cut, "A");
        folder.ClassList.Should().Contain("spb-metric-row--folder");
        folder.QuerySelector(".spb-metric-label")!.TextContent.Should().Be("A");
        folder.QuerySelector(".spb-metric-count")!.TextContent.Should().Be("2");
        cut.FindAll("tr.spb-metric-row").Should().ContainSingle();

        cut.Find(".spb-metric-toggle").Click();

        cut.FindAll("tr.spb-metric-row").Select(row => row.QuerySelector(".spb-td-name")!.GetAttribute("title"))
            .Should().Equal("A", "A/T1", "A/T2");
    }

    [Test]
    public void HierarchyMode_ExpandsSingleChildChainsOnFirstRender()
    {
        var cut = RenderTable(
            SparkplugMetricViewMode.Hierarchy,
            Metric("Vancouver/Area1/Line1/Zone1/Asset1/T1", "23.4"));

        Row(cut, "Vancouver/Area1/Line1/Zone1/Asset1/T1")
            .QuerySelector(".spb-td-value")!.TextContent.Trim().Should().Be("23.4");
    }

    [Test]
    public void HierarchyMode_MetricWithChildren_ShowsItsOwnValueAndKeepsTheChild()
    {
        var cut = RenderTable(SparkplugMetricViewMode.Hierarchy, Metric("A", "10"), Metric("A/B", "20"));

        var a = Row(cut, "A");
        a.QuerySelector(".spb-td-value")!.TextContent.Trim().Should().Be("10");
        a.QuerySelector(".spb-metric-toggle").Should().NotBeNull();
        Row(cut, "A/B").QuerySelector(".spb-td-value")!.TextContent.Trim().Should().Be("20");
    }

    [Test]
    public async Task HierarchyMode_ChartUsesTheExactFullMetricName()
    {
        SparkplugChartSelection? captured = null;
        var cut = Render<SparkplugMetricsTable>(parameters => parameters
            .Add(component => component.Items, [Metric("Vancouver/Area1/T1", "23.4")])
            .Add(component => component.Topic, Topic)
            .Add(component => component.Mode, SparkplugMetricViewMode.Hierarchy)
            .Add(component => component.OnAddToChart,
                EventCallback.Factory.Create<SparkplugChartSelection>(this, selection => captured = selection)));

        await cut.InvokeAsync(() => Row(cut, "Vancouver/Area1/T1")
            .QuerySelector("button[title='Add to chart']")!.Click());

        captured.Should().NotBeNull();
        captured!.Topic.Should().Be(Topic);
        captured.JsonPath.Should().Be("metrics.Vancouver/Area1/T1");
        captured.SeriesName.Should().Be("Vancouver/Area1/T1");
    }

    [Test]
    public async Task HierarchyMode_ChartKeepsEmptySegmentIdentity()
    {
        SparkplugChartSelection? captured = null;
        var cut = Render<SparkplugMetricsTable>(parameters => parameters
            .Add(component => component.Items, [Metric("A//B", "23.4")])
            .Add(component => component.Topic, Topic)
            .Add(component => component.Mode, SparkplugMetricViewMode.Hierarchy)
            .Add(component => component.OnAddToChart,
                EventCallback.Factory.Create<SparkplugChartSelection>(this, selection => captured = selection)));

        await cut.InvokeAsync(() => Row(cut, "A//B")
            .QuerySelector("button[title='Add to chart']")!.Click());

        captured.Should().NotBeNull();
        captured!.JsonPath.Should().Be("metrics.A//B");
        captured.SeriesName.Should().Be("A//B");
    }

    [Test]
    public void HierarchyMode_SplitsParenthesesSlashesButKeepsTheExactFullName()
    {
        var cut = RenderTable(SparkplugMetricViewMode.Hierarchy, Metric("Flow Rate (L/min)", "12.5"));

        var leaf = Row(cut, "Flow Rate (L/min)");
        leaf.QuerySelector(".spb-metric-label")!.TextContent.Should().Be("min)");
        leaf.QuerySelector(".spb-td-name")!.GetAttribute("title").Should().Be("Flow Rate (L/min)");
    }

    [Test]
    public void HierarchyMode_RendersCompactMetaForNarrowDetailLayouts()
    {
        var cut = RenderTable(
            SparkplugMetricViewMode.Hierarchy,
            new SpbMetricSnapshot("A/T1", "double", "23.4", DateTime.UtcNow, 42));

        var meta = Row(cut, "A/T1").QuerySelector(".spb-metric-meta")!;
        meta.QuerySelector(".spb-metric-meta-type")!.TextContent.Should().Be("double");
        meta.QuerySelector(".spb-metric-meta-alias")!.TextContent.Trim().Should().Be("Alias 42");
        meta.QuerySelector(".spb-metric-meta-updated")!.TextContent.Should().NotBeNullOrWhiteSpace();
    }

    [Test]
    public void FlatMode_ShowsTheFullNameWithoutFoldersOrIndent()
    {
        var cut = RenderTable(SparkplugMetricViewMode.Flat, Metric("A/T1"));

        var row = Row(cut, "A/T1");
        row.QuerySelector(".spb-metric-label")!.TextContent.Should().Be("A/T1");
        row.QuerySelector(".spb-metric-toggle").Should().BeNull();
        cut.FindAll(".spb-metric-indent").Should().BeEmpty();
    }

    [Test]
    public void HierarchyMode_SkipsNamelessMetrics()
    {
        var cut = RenderTable(SparkplugMetricViewMode.Hierarchy, Metric(""), Metric("   "), Metric("A/T1"));

        cut.FindAll("tr.spb-metric-row").Select(row => row.QuerySelector(".spb-td-name")!.GetAttribute("title"))
            .Should().Equal("A", "A/T1");
    }

    [Test]
    public void HierarchyMode_DeepNamesKeepBoundedIndentAndTheFullPathTitle()
    {
        const string fullPath = "A/B/C/D/E/F/G/H/I/J/K/T1";
        var cut = RenderTable(SparkplugMetricViewMode.Hierarchy, Metric(fullPath));

        var row = Row(cut, fullPath);
        row.QuerySelector(".spb-td-name")!.GetAttribute("title").Should().Be(fullPath);
        row.QuerySelector(".spb-metric-indent")!.GetAttribute("width").Should().Be("100");
        row.QuerySelector(".spb-td-name")!.GetAttribute("style").Should().BeNull();
    }

    [Test]
    public void FlatMode_KeepsParenthesesNameAsOneLabel()
    {
        var cut = RenderTable(SparkplugMetricViewMode.Flat, Metric("Flow Rate (L/min)", "12.5"));

        var row = Row(cut, "Flow Rate (L/min)");
        row.QuerySelector(".spb-metric-label")!.TextContent.Should().Be("Flow Rate (L/min)");
        row.QuerySelector(".spb-metric-toggle").Should().BeNull();
    }

    [Test]
    public void FlatMode_OrdersCaseAndZeroPaddingTiesOrdinal()
    {
        var cut = RenderTable(
            SparkplugMetricViewMode.Flat,
            Metric("A2/T1"),
            Metric("A02/T1"),
            Metric("a2/T1"));

        cut.FindAll("tr.spb-metric-row").Select(row => row.QuerySelector(".spb-td-name")!.GetAttribute("title"))
            .Should().Equal("A02/T1", "A2/T1", "a2/T1");
    }

    [Test]
    public void HierarchyMode_ShowsControlMetricsWithoutCommandButtons()
    {
        var cut = RenderTable(
            SparkplugMetricViewMode.Hierarchy,
            Metric("Node Control/Rebirth", "true", "bool"));

        var leaf = Row(cut, "Node Control/Rebirth");
        leaf.QuerySelector(".spb-metric-label")!.TextContent.Should().Be("Rebirth");
        leaf.QuerySelectorAll("button").Select(button => button.TextContent.Trim())
            .Should().NotContain("Request rebirth")
            .And.NotContain("Reboot");
        leaf.QuerySelector("button[title='Add to chart']").Should().NotBeNull();
    }

    private IRenderedComponent<SparkplugMetricsTable> RenderTable(
        SparkplugMetricViewMode mode,
        params SpbMetricSnapshot[] metrics) =>
        Render<SparkplugMetricsTable>(parameters => parameters
            .Add(component => component.Items, metrics)
            .Add(component => component.Topic, Topic)
            .Add(component => component.Mode, mode)
            .Add(component => component.OnAddToChart,
                EventCallback.Factory.Create<SparkplugChartSelection>(this, _ => { })));

    private static IElement Row(IRenderedComponent<SparkplugMetricsTable> cut, string fullPath) =>
        cut.FindAll("tr.spb-metric-row")
            .Single(row => row.QuerySelector(".spb-td-name")!.GetAttribute("title") == fullPath);

    private static string VisibleLabel(IRenderedComponent<SparkplugMetricsTable> cut, string fullPath) =>
        Row(cut, fullPath).QuerySelector(".spb-metric-label")!.TextContent.Trim();

    private static SpbMetricSnapshot Metric(string name, string value = "1", string dataType = "double") =>
        new(name, dataType, value, DateTime.UtcNow);
}
