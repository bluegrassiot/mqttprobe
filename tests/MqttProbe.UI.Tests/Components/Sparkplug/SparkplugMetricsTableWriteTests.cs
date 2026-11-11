using AngleSharp.Dom;
using Microsoft.AspNetCore.Components;
using MqttProbe.Core.Models.Sparkplug;
using MqttProbe.UI.Tests.TestHelpers;

namespace MqttProbe.UI.Tests.Components.Sparkplug;

[TestFixture]
public sealed class SparkplugMetricsTableWriteTests : BunitTestContext
{
    private const string Topic = "spBv1.0/factory/NDATA/edge-01";
    private const string WriteSelector = "button.spb-write-btn";

    [SetUp]
    public void Setup() => EnsureMudProviders();

    [TestCase(SparkplugMetricViewMode.Flat)]
    [TestCase(SparkplugMetricViewMode.Hierarchy)]
    public void WriteButton_AppearsOnMetricRowsOnly(SparkplugMetricViewMode mode)
    {
        var cut = RenderTable(
            mode, allowWrites: true,
            Metric("Line", "int32"),
            Metric("Line/Speed", "double"),
            Metric("Tank/Level", "float"),
            Metric("Tank/Name", "string"));

        Row(cut, "Line").QuerySelector(WriteSelector).Should().NotBeNull("a metric with children is still a metric");
        Row(cut, "Line/Speed").QuerySelector(WriteSelector).Should().NotBeNull();

        if (mode == SparkplugMetricViewMode.Hierarchy)
        {
            var folder = Row(cut, "Tank");
            folder.ClassList.Should().Contain("spb-metric-row--folder");
            folder.QuerySelector(WriteSelector).Should().BeNull();
        }
        else
        {
            Row(cut, "Tank/Level").QuerySelector(WriteSelector).Should().NotBeNull();
            Row(cut, "Tank/Name").QuerySelector(WriteSelector).Should().NotBeNull();
        }
    }

    [TestCase("int8")]
    [TestCase("int16")]
    [TestCase("int32")]
    [TestCase("int64")]
    [TestCase("uint8")]
    [TestCase("uint16")]
    [TestCase("uint32")]
    [TestCase("uint64")]
    [TestCase("float")]
    [TestCase("double")]
    [TestCase("bool")]
    [TestCase("string")]
    [TestCase("text")]
    [TestCase("uuid")]
    [TestCase("datetime")]
    public void WriteButton_ShownForSupportedTypes(string dataType)
    {
        var cut = RenderTable(SparkplugMetricViewMode.Flat, allowWrites: true, Metric("M", dataType));

        Row(cut, "M").QuerySelector(WriteSelector).Should().NotBeNull();
    }

    [TestCase("bytes")]
    [TestCase("dataset")]
    [TestCase("template")]
    [TestCase("file")]
    [TestCase("propertyset")]
    [TestCase("propertysetlist")]
    [TestCase("int32[]")]
    [TestCase("string[]")]
    [TestCase("unknown")]
    public void WriteButton_HiddenForUnsupportedTypes(string dataType)
    {
        var cut = RenderTable(SparkplugMetricViewMode.Flat, allowWrites: true, Metric("M", dataType));

        Row(cut, "M").QuerySelector(WriteSelector).Should().BeNull();
    }

    [TestCase("Node Control/Reboot")]
    [TestCase("Node Control/Rebirth")]
    [TestCase("Node Control/Next Server")]
    [TestCase("Node Control/Scan Rate")]
    [TestCase("Device Control/Reboot")]
    [TestCase("Device Control/Rebirth")]
    [TestCase("Device Control/Scan Rate")]
    [TestCase("node control/reboot")]
    public void WriteButton_HiddenForControlMetrics(string name)
    {
        var cut = RenderTable(SparkplugMetricViewMode.Flat, allowWrites: true, Metric(name, "bool"));

        Row(cut, name).QuerySelector(WriteSelector).Should().BeNull();
    }

    [Test]
    public void WriteButton_WhenWritesAreOff_IsDisabledAndPointsAtTheSetting()
    {
        var cut = RenderTable(SparkplugMetricViewMode.Flat, allowWrites: false, Metric("Setpoint", "int32"));

        var button = Row(cut, "Setpoint").QuerySelector(WriteSelector)!;
        button.HasAttribute("disabled").Should().BeTrue();
        button.ParentElement!.GetAttribute("title")
            .Should().Be("Enable Allow metric writes in Settings, Sparkplug");
    }

    [TestCase(SparkplugMetricViewMode.Flat)]
    [TestCase(SparkplugMetricViewMode.Hierarchy)]
    public async Task WriteButton_Click_ReportsTheExactMetric(SparkplugMetricViewMode mode)
    {
        SpbMetricSnapshot? captured = null;
        var metric = Metric("Line/Speed", "double");
        var cut = Render<SparkplugMetricsTable>(parameters => parameters
            .Add(component => component.Items, [metric])
            .Add(component => component.Topic, Topic)
            .Add(component => component.Mode, mode)
            .Add(component => component.AllowMetricWrites, true)
            .Add(component => component.OnWriteMetric,
                EventCallback.Factory.Create<SpbMetricSnapshot>(this, snapshot => captured = snapshot)));

        var button = Row(cut, "Line/Speed").QuerySelector(WriteSelector)!;
        button.GetAttribute("aria-label").Should().Be("Write metric Line/Speed");
        await cut.InvokeAsync(() => button.Click());

        captured.Should().BeSameAs(metric);
    }

    [Test]
    public void WriteButton_NotRenderedWithoutAHandler()
    {
        var cut = Render<SparkplugMetricsTable>(parameters => parameters
            .Add(component => component.Items, [Metric("Setpoint", "int32")])
            .Add(component => component.Topic, Topic)
            .Add(component => component.AllowMetricWrites, true));

        cut.FindAll(WriteSelector).Should().BeEmpty();
    }

    private IRenderedComponent<SparkplugMetricsTable> RenderTable(
        SparkplugMetricViewMode mode, bool allowWrites, params SpbMetricSnapshot[] metrics) =>
        Render<SparkplugMetricsTable>(parameters => parameters
            .Add(component => component.Items, metrics)
            .Add(component => component.Topic, Topic)
            .Add(component => component.Mode, mode)
            .Add(component => component.AllowMetricWrites, allowWrites)
            .Add(component => component.OnWriteMetric,
                EventCallback.Factory.Create<SpbMetricSnapshot>(this, _ => { })));

    private static IElement Row(IRenderedComponent<SparkplugMetricsTable> cut, string fullPath) =>
        cut.FindAll("tr.spb-metric-row").Single(row =>
            row.QuerySelector(".spb-td-name")?.GetAttribute("title") == fullPath);

    private static SpbMetricSnapshot Metric(string name, string dataType) =>
        new(name, dataType, "0", DateTime.UtcNow);
}
