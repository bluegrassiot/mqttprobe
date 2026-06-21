using MqttProbe.Components.Emulation;
using MqttProbe.Models.Emulation;
using MqttProbe.Shared.Tests.TestHelpers;

namespace MqttProbe.Shared.Tests.Components.Emulation;

[TestFixture]
public class WaveformPreviewTests : BunitTestContext
{
    private static EmulatorMetricConfig Metric(
        WaveformKind kind,
        MetricValueType valueType = MetricValueType.Double,
        double min = 10,
        double max = 30) =>
        new() { Waveform = kind, ValueType = valueType, Min = min, Max = max };

    private IRenderedComponent<WaveformPreview> RenderPreview(EmulatorMetricConfig metric) =>
        Render<WaveformPreview>(ps => ps.Add(p => p.Metric, metric));

    private static string[] Points(IRenderedComponent<WaveformPreview> cut) =>
        cut.Find("polyline").GetAttribute("points")!
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

    [Test]
    public void Sine_Renders64PointPolyline()
    {
        var cut = RenderPreview(Metric(WaveformKind.Sine));

        Points(cut).Should().HaveCount(64);
    }

    [Test]
    public void Toggle_RendersStepTraceWithDuplicatedTransitionPoints()
    {
        var cut = RenderPreview(Metric(WaveformKind.Toggle, MetricValueType.Boolean));

        var points = Points(cut);
        points.Length.Should().BeGreaterThan(64);
        points.Select(p => p.Split(',')[1]).Distinct().Should().HaveCount(2);
    }

    [Test]
    public void SameMetric_TwoRenders_ProduceIdenticalMarkup()
    {
        var metric = Metric(WaveformKind.RandomWalk);

        var first = RenderPreview(metric).Markup;
        var second = RenderPreview(metric).Markup;

        second.Should().Be(first);
    }

    [Test]
    public void Trace_UsesTealStrokeAndFixedViewBox()
    {
        var cut = RenderPreview(Metric(WaveformKind.Sine));

        var svg = cut.Find("svg");
        svg.GetAttribute("viewBox").Should().Be("0 0 220 48");
        svg.GetAttribute("preserveAspectRatio").Should().Be("none");
        var polyline = cut.Find("polyline");
        polyline.GetAttribute("stroke").Should().Be("#2DD4BF");
        polyline.GetAttribute("vector-effect").Should().Be("non-scaling-stroke");
    }

    [Test]
    public void Labels_ShowSampleMinAndMaxInMono()
    {
        var cut = RenderPreview(Metric(WaveformKind.Sine, min: 10, max: 30));

        cut.Find(".emu-waveform__label--max").TextContent.Should().Be("30");
        cut.Find(".emu-waveform__label--min").TextContent.Should().Be("10");
    }

    [Test]
    public void FixedBooleanTrue_DrawsAtTopOfFixedZeroOneRange()
    {
        var metric = Metric(WaveformKind.FixedBoolean, MetricValueType.Boolean);
        metric.BooleanValue = true;

        var cut = RenderPreview(metric);

        Points(cut).Should().OnlyContain(p => p.EndsWith(",0"));
    }

    [Test]
    public void Constant_DrawsFlatMidline()
    {
        var metric = Metric(WaveformKind.Constant);
        metric.ConstantValue = 42.5;

        var cut = RenderPreview(metric);

        Points(cut).Should().OnlyContain(p => p.EndsWith(",24"));
        cut.Find(".emu-waveform__label--max").TextContent.Should().Be("42.5");
        cut.Find(".emu-waveform__label--min").TextContent.Should().Be("42.5");
    }
}
