using Microsoft.AspNetCore.Components;
using MqttProbe.Core.Models.Sparkplug;
using MqttProbe.UI.Tests.TestHelpers;

namespace MqttProbe.UI.Tests.Components.Sparkplug;

[TestFixture]
public class SparkplugMetricsTableRowIdentityTests : BunitTestContext
{
    [SetUp]
    public void Setup() => EnsureMudProviders();

    [Test]
    public void ValueUpdate_KeepsRowAndChartButton()
    {
        var first = new SpbMetricSnapshot("Flow Rate", "double", "12.5000", DateTime.UtcNow);
        var cut = RenderTable(first);
        var chartButton = cut.FindComponent<AddToChartButton>().Instance;

        var updated = first with { Value = "13.2500", LastUpdated = first.LastUpdated.AddSeconds(1) };
        cut.Render(parameters => parameters.Add(component => component.Items, [updated]));

        var retained = cut.FindComponent<AddToChartButton>().Instance;
        retained.Should().BeSameAs(chartButton, "the row keeps its chart button while the value changes");
        retained.JsonPath.Should().Be("metrics.Flow Rate");
        cut.FindAll("tr.spb-metric-row").Should().ContainSingle();
        cut.Find(".spb-td-value").TextContent.Trim().Should().Be("13.2500");
    }

    [Test]
    public void RemovedMetric_DisappearsAndComesBackWhenItReturns()
    {
        var flow = new SpbMetricSnapshot("Flow Rate", "double", "12.5000", DateTime.UtcNow);
        var pressure = new SpbMetricSnapshot("Pressure", "double", "1.0000", DateTime.UtcNow);
        var cut = RenderTable(flow, pressure);

        cut.Render(parameters => parameters.Add(component => component.Items, [flow]));
        cut.FindAll("tr.spb-metric-row").Should().ContainSingle()
            .Which.QuerySelector(".spb-td-name")!.TextContent.Trim().Should().Be("Flow Rate");
        cut.Instance.CachedRowCountForTest.Should().Be(1, "the dropped metric must not stay cached");

        cut.Render(parameters => parameters.Add(component => component.Items, [flow, pressure]));
        cut.FindAll("tr.spb-metric-row").Should().HaveCount(2);
        cut.Instance.CachedRowCountForTest.Should().Be(2);
    }

    [Test]
    public void EqualCountNameReplacement_ForgetsTheOldMetrics()
    {
        var flow = new SpbMetricSnapshot("Flow Rate", "double", "12.5000", DateTime.UtcNow);
        var pressure = new SpbMetricSnapshot("Pressure", "double", "1.0000", DateTime.UtcNow);
        var torque = new SpbMetricSnapshot("Torque", "double", "4.0000", DateTime.UtcNow);
        var speed = new SpbMetricSnapshot("Speed", "double", "900.0000", DateTime.UtcNow);
        var cut = RenderTable(flow, pressure);

        // Same count, different names: a node/device switch that shares no metric with the last one.
        cut.Render(parameters => parameters.Add(component => component.Items, [torque, speed]));

        cut.FindAll("tr.spb-metric-row").Select(row => row.QuerySelector(".spb-td-name")!.TextContent.Trim())
            .Should().BeEquivalentTo(["Torque", "Speed"]);
        cut.Instance.CachedRowCountForTest.Should().Be(2,
            "replacing the whole metric set must not keep the previous rows cached");
    }

    [Test]
    public void ValueColourClasses_ReplaceTheInlineStyle()
    {
        var numeric = new SpbMetricSnapshot("Flow Rate", "double", "12.5000", DateTime.UtcNow);
        var text = new SpbMetricSnapshot("Mode", "string", "auto", DateTime.UtcNow);
        var cut = RenderTable(numeric, text);

        var numericCell = MetricCell(cut, "Flow Rate");
        numericCell.ClassList.Should().Contain("spb-td-value--numeric");
        numericCell.ClassList.Should().NotContain("spb-td-value--text");
        numericCell.GetAttribute("style").Should().BeNull();

        var textCell = MetricCell(cut, "Mode");
        textCell.ClassList.Should().Contain("spb-td-value--text");
        textCell.ClassList.Should().NotContain("spb-td-value--numeric");
        textCell.GetAttribute("style").Should().BeNull();
    }

    private static AngleSharp.Dom.IElement MetricCell(
        IRenderedComponent<SparkplugMetricsTable> cut, string name) =>
        cut.FindAll("tr.spb-metric-row")
            .Single(row => row.QuerySelector(".spb-td-name")!.TextContent.Trim() == name)
            .QuerySelector(".spb-td-value")!;

    private IRenderedComponent<SparkplugMetricsTable> RenderTable(params SpbMetricSnapshot[] metrics) =>
        Render<SparkplugMetricsTable>(parameters => parameters
            .Add(component => component.Items, metrics)
            .Add(component => component.Topic, "spBv1.0/factory/NDATA/edge-01")
            .Add(component => component.OnAddToChart,
                EventCallback.Factory.Create<SparkplugChartSelection>(this, _ => { })));
}
