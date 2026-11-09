using AngleSharp.Dom;
using Microsoft.AspNetCore.Components;
using MqttProbe.Core.Models.Sparkplug;
using MqttProbe.UI.Tests.TestHelpers;

namespace MqttProbe.UI.Tests.Components.Sparkplug;

[TestFixture]
public sealed class SparkplugMetricsTableTreeClarityTests : BunitTestContext
{
    private const string Topic = "spBv1.0/factory/DDATA/edge-01/sensor-A";

    [SetUp]
    public void Setup() => EnsureMudProviders();

    [Test]
    public void ExpandedGroup_ReturnsToRootForTheNextStandaloneMetric()
    {
        var cut = RenderTable(
            Metric("System/Heap"),
            Metric("System/MqttConnected"),
            Metric("System/Rssi"),
            Metric("System/Uptime"),
            Metric("TempDelta"));

        Row(cut, "System").ClassList.Should().Contain("spb-metric-row--folder");
        Row(cut, "TempDelta").ClassList.Should().NotContain("spb-metric-row--folder");

        Toggle(cut, "System");

        Titles(cut).Should().Equal(
            "System", "System/Heap", "System/MqttConnected", "System/Rssi", "System/Uptime", "TempDelta");

        Indent(cut, "System").Should().Be(0);
        Indent(cut, "System/Heap").Should().Be(24);
        Indent(cut, "System/MqttConnected").Should().Be(24);
        Indent(cut, "System/Rssi").Should().Be(24);
        Indent(cut, "System/Uptime").Should().Be(24);
        Indent(cut, "TempDelta").Should().Be(0, "a standalone root metric must align with the root folder, not its children");
    }

    [Test]
    public void CollapsedGroup_KeepsChildrenHiddenAndLeavesNoGroupEndMarker()
    {
        var cut = RenderTable(
            Metric("System/Heap"),
            Metric("System/Rssi"),
            Metric("TempDelta"));

        Toggle(cut, "System");
        Titles(cut).Should().Equal("System", "System/Heap", "System/Rssi", "TempDelta");

        Toggle(cut, "System");

        Titles(cut).Should().Equal("System", "TempDelta");
        Indent(cut, "System").Should().Be(0);
        Indent(cut, "TempDelta").Should().Be(0);
        cut.FindAll("tr.spb-metric-row--group-end").Should().BeEmpty(
            "nothing is nested after the group collapses");
    }

    [Test]
    public void NestedGroups_MarkEachReturnToAShallowerDepth()
    {
        var cut = RenderTable(
            Metric("A/B/T1"),
            Metric("A/B/T2"),
            Metric("A/C/T3"),
            Metric("A/C/T4"),
            Metric("D/T4"),
            Metric("D/T5"));

        Toggle(cut, "A");
        Toggle(cut, "A/B");

        Titles(cut).Should().Equal(
            "A", "A/B", "A/B/T1", "A/B/T2", "A/C", "D");

        Indent(cut, "A").Should().Be(0);
        Indent(cut, "A/B").Should().Be(24);
        Indent(cut, "A/B/T1").Should().Be(48);
        Indent(cut, "A/B/T2").Should().Be(48);
        Indent(cut, "A/C").Should().Be(24);
        Indent(cut, "D").Should().Be(0);

        Row(cut, "A/B/T1").ClassList.Should().NotContain("spb-metric-row--group-end");
        Row(cut, "A/B/T2").ClassList.Should().Contain("spb-metric-row--group-end",
            "the last child of the inner B group ends that group");
        Row(cut, "A/C").ClassList.Should().Contain("spb-metric-row--group-end",
            "C is the last child of the A group");
        Row(cut, "D").ClassList.Should().NotContain("spb-metric-row--group-end",
            "a root row does not end a nested group");
    }

    [Test]
    public void FlatMode_KeepsFullNamesWithoutIndentOrGroupMarkers()
    {
        var cut = RenderTable(
            SparkplugMetricViewMode.Flat,
            Metric("A/B/T1"),
            Metric("A/B/T2"),
            Metric("TempDelta"));

        cut.FindAll(".spb-metric-indent").Should().BeEmpty();
        cut.FindAll("tr.spb-metric-row--group-end").Should().BeEmpty();
        cut.FindAll(".spb-metric-label").Select(label => label.TextContent.Trim())
            .Should().Equal("A/B/T1", "A/B/T2", "TempDelta");
    }

    private static void Toggle(IRenderedComponent<SparkplugMetricsTable> cut, string fullPath) =>
        Row(cut, fullPath).QuerySelector(".spb-metric-toggle")!.Click();

    private static int Indent(IRenderedComponent<SparkplugMetricsTable> cut, string fullPath) =>
        int.Parse(Row(cut, fullPath).QuerySelector(".spb-metric-indent")!.GetAttribute("width")!);

    private static IElement Row(IRenderedComponent<SparkplugMetricsTable> cut, string fullPath) =>
        cut.FindAll("tr.spb-metric-row")
            .Single(row => row.QuerySelector(".spb-td-name")!.GetAttribute("title") == fullPath);

    private static IEnumerable<string?> Titles(IRenderedComponent<SparkplugMetricsTable> cut) =>
        cut.FindAll("tr.spb-metric-row").Select(row => row.QuerySelector(".spb-td-name")!.GetAttribute("title"));

    private IRenderedComponent<SparkplugMetricsTable> RenderTable(params SpbMetricSnapshot[] metrics) =>
        RenderTable(SparkplugMetricViewMode.Hierarchy, metrics);

    private IRenderedComponent<SparkplugMetricsTable> RenderTable(
        SparkplugMetricViewMode mode,
        params SpbMetricSnapshot[] metrics) =>
        Render<SparkplugMetricsTable>(parameters => parameters
            .Add(component => component.Items, metrics)
            .Add(component => component.Topic, Topic)
            .Add(component => component.Mode, mode)
            .Add(component => component.OnAddToChart,
                EventCallback.Factory.Create<SparkplugChartSelection>(this, _ => { })));

    private static SpbMetricSnapshot Metric(string name, string value = "1", string dataType = "double") =>
        new(name, dataType, value, DateTime.UtcNow);
}
