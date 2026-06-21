using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Components.Emulation;
using MqttProbe.Models.Emulation;
using MqttProbe.Services.Emulation;
using MqttProbe.Shared.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.Shared.Tests.Components.Emulation;

[TestFixture]
public class EmulatorMetricRowTests : BunitTestContext
{
    private IEmulationService _mockService = null!;
    private EmulatorNodeConfig _node = null!;
    private EmulatorDeviceConfig _device = null!;
    private EmulatorMetricConfig _metric = null!;
    private int _changedCount;

    [SetUp]
    public void SetupMocks()
    {
        _metric = new EmulatorMetricConfig { Name = "Flow Rate" };
        _device = new EmulatorDeviceConfig { DeviceId = "Sensor-1", Metrics = [_metric] };
        _node = new EmulatorNodeConfig { NodeId = "Press-01", Devices = [_device] };
        _changedCount = 0;
        _mockService = Substitute.For<IEmulationService>();
        _mockService.IsRunning.Returns(false);
        Services.AddSingleton(_mockService);
        EnsureMudProviders();
    }

    private IRenderedComponent<EmulatorMetricRow> RenderRow() =>
        Render<EmulatorMetricRow>(ps => ps
            .Add(p => p.Node, _node)
            .Add(p => p.Device, _device)
            .Add(p => p.Metric, _metric)
            .Add(p => p.OnChanged, () => _changedCount++));

    private static IReadOnlyList<string> NumericFieldLabels(IRenderedComponent<EmulatorMetricRow> cut) =>
        cut.FindComponents<MudNumericField<double>>().Select(f => f.Instance.Label!).ToList();

    [Test]
    public void SineMetric_ShowsMinMaxAndPeriodFields()
    {
        _metric.Waveform = WaveformKind.Sine;

        var labels = NumericFieldLabels(RenderRow());

        labels.Should().BeEquivalentTo("Min", "Max", "Period (s)");
    }

    [Test]
    public void RampMetric_ShowsMinMaxAndPeriodFields()
    {
        _metric.Waveform = WaveformKind.Ramp;

        var labels = NumericFieldLabels(RenderRow());

        labels.Should().BeEquivalentTo("Min", "Max", "Period (s)");
    }

    [Test]
    public void RandomWalkMetric_ShowsMinMaxAndStepFields()
    {
        _metric.Waveform = WaveformKind.RandomWalk;

        var labels = NumericFieldLabels(RenderRow());

        labels.Should().BeEquivalentTo("Min", "Max", "Step amplitude");
    }

    [Test]
    public void ConstantMetric_ShowsOnlyValueField()
    {
        _metric.Waveform = WaveformKind.Constant;

        var labels = NumericFieldLabels(RenderRow());

        labels.Should().BeEquivalentTo("Value");
    }

    [Test]
    public void ToggleMetric_ShowsOnlyPeriodField()
    {
        _metric.Waveform = WaveformKind.Toggle;
        _metric.ValueType = MetricValueType.Boolean;

        var labels = NumericFieldLabels(RenderRow());

        labels.Should().BeEquivalentTo("Period (s)");
    }

    [Test]
    public void RandomBooleanMetric_ShowsOnlyProbabilityField()
    {
        _metric.Waveform = WaveformKind.RandomBoolean;
        _metric.ValueType = MetricValueType.Boolean;

        var labels = NumericFieldLabels(RenderRow());

        labels.Should().BeEquivalentTo("True probability");
    }

    [Test]
    public void FixedBooleanMetric_ShowsValueSwitch()
    {
        _metric.Waveform = WaveformKind.FixedBoolean;
        _metric.ValueType = MetricValueType.Boolean;

        var cut = RenderRow();

        cut.FindComponents<MudSwitch<bool>>().Should().ContainSingle();
        NumericFieldLabels(cut).Should().BeEmpty();
    }

    [Test]
    public void MetricRow_RendersWaveformPreview()
    {
        var cut = RenderRow();

        cut.FindComponents<WaveformPreview>().Should().ContainSingle();
    }

    [Test]
    public async Task MaxCommit_OnRandomWalk_RecomputesPreviewPoints()
    {
        _metric.Waveform = WaveformKind.RandomWalk;
        _metric.Min = 0;
        _metric.Max = 100;
        _metric.StepAmplitude = 5;
        var cut = RenderRow();
        var pointsBefore = cut.Find("polyline").GetAttribute("points");

        var maxField = cut.FindComponents<MudNumericField<double>>()
            .Single(f => f.Instance.Label == "Max");
        await cut.InvokeAsync(() => maxField.Instance.ValueChanged.InvokeAsync(12));

        cut.Find("polyline").GetAttribute("points").Should().NotBe(pointsBefore);
        _changedCount.Should().BeGreaterThan(0);
    }

    [Test]
    public async Task MaxCommit_OnSine_UpdatesPreviewMaxLabel()
    {
        _metric.Waveform = WaveformKind.Sine;
        _metric.Min = 0;
        _metric.Max = 100;
        var cut = RenderRow();

        var maxField = cut.FindComponents<MudNumericField<double>>()
            .Single(f => f.Instance.Label == "Max");
        await cut.InvokeAsync(() => maxField.Instance.ValueChanged.InvokeAsync(60));

        cut.Find(".emu-waveform__label--max").TextContent.Should().Be("60");
    }

    [Test]
    public void MinNotBelowMax_ShowsValidationError()
    {
        _metric.Waveform = WaveformKind.Sine;
        _metric.Min = 50;
        _metric.Max = 10;

        var cut = RenderRow();

        cut.Markup.Should().Contain("Must be greater than Min");
    }

    [Test]
    public void EmptyMetricName_ShowsValidationError()
    {
        _metric.Name = "";

        var cut = RenderRow();

        cut.Markup.Should().Contain("Required");
    }

    [Test]
    public void DuplicateMetricNameInDevice_ShowsValidationError()
    {
        _device.Metrics.Add(new EmulatorMetricConfig { Name = "Flow Rate" });

        var cut = RenderRow();

        cut.Markup.Should().Contain("already used in this device");
    }

    [Test]
    public void GenericNode_MetricNameWithTopicChars_ShowsValidationError()
    {
        _node.Type = EmulatorNodeType.Generic;
        _metric.Name = "Flow/Rate";

        var cut = RenderRow();

        cut.Markup.Should().Contain("Must not contain / + #");
    }

    [Test]
    public async Task ValueTypeSwitchToBoolean_CoercesWaveformToBooleanKind()
    {
        _metric.Waveform = WaveformKind.Sine;
        var cut = RenderRow();

        var typeSelect = cut.FindComponent<MudSelect<MetricValueType>>();
        await cut.InvokeAsync(() => typeSelect.Instance.ValueChanged.InvokeAsync(MetricValueType.Boolean));

        _metric.Waveform.Should().Be(WaveformKind.FixedBoolean);
        _changedCount.Should().BeGreaterThan(0);
    }

    [Test]
    public async Task ValueTypeSwitchToDouble_CoercesBooleanWaveformToNumericKind()
    {
        _metric.ValueType = MetricValueType.Boolean;
        _metric.Waveform = WaveformKind.Toggle;
        var cut = RenderRow();

        var typeSelect = cut.FindComponent<MudSelect<MetricValueType>>();
        await cut.InvokeAsync(() => typeSelect.Instance.ValueChanged.InvokeAsync(MetricValueType.Double));

        _metric.Waveform.Should().Be(WaveformKind.Sine);
    }

    [Test]
    public async Task DeleteButton_RemovesMetricFromDeviceAndNotifies()
    {
        var cut = RenderRow();

        cut.Find("button[title='Delete metric']").Click();
        await cut.InvokeAsync(() => Task.CompletedTask);

        _device.Metrics.Should().BeEmpty();
        _changedCount.Should().BeGreaterThan(0);
    }

    [Test]
    public void WhileRunning_FieldsAreDisabled()
    {
        _mockService.IsRunning.Returns(true);

        var cut = RenderRow();

        cut.FindComponents<MudNumericField<double>>()
            .Should().OnlyContain(f => f.Instance.Disabled);
        cut.Find("button[title='Delete metric']").HasAttribute("disabled").Should().BeTrue();
    }
}
