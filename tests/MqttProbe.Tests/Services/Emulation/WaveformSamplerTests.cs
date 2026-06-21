using MqttProbe.Models.Emulation;
using MqttProbe.Services.Emulation;

namespace MqttProbe.Shared.Tests.Services.Emulation;

[TestFixture]
public class WaveformSamplerTests
{
    private static EmulatorMetricConfig Metric(
        WaveformKind kind,
        MetricValueType valueType = MetricValueType.Double,
        double min = 10,
        double max = 30,
        double periodSeconds = 60,
        double stepAmplitude = 1,
        double constantValue = 0,
        bool booleanValue = false,
        double trueProbability = 0.5) =>
        new()
        {
            Waveform = kind,
            ValueType = valueType,
            Min = min,
            Max = max,
            PeriodSeconds = periodSeconds,
            StepAmplitude = stepAmplitude,
            ConstantValue = constantValue,
            BooleanValue = booleanValue,
            TrueProbability = trueProbability
        };

    [Test]
    public void Next_Sine_StaysWithinBounds()
    {
        var metric = Metric(WaveformKind.Sine);
        var state = WaveformSampler.CreateState(metric);

        for (var t = 0.0; t <= 120.0; t += 0.5)
        {
            var value = WaveformSampler.Next(metric, state, t);
            value.Should().BeInRange(10, 30);
        }
    }

    [Test]
    public void Next_Sine_AtTimeZero_ReturnsMidpoint()
    {
        var metric = Metric(WaveformKind.Sine);
        var state = WaveformSampler.CreateState(metric);

        WaveformSampler.Next(metric, state, 0).Should().BeApproximately(20, 1e-9);
    }

    [Test]
    public void Next_Sine_AtQuarterPeriod_ReturnsMax()
    {
        var metric = Metric(WaveformKind.Sine);
        var state = WaveformSampler.CreateState(metric);

        WaveformSampler.Next(metric, state, 15).Should().BeApproximately(30, 1e-9);
    }

    [Test]
    public void Next_Ramp_AtTimeZero_ReturnsMin()
    {
        var metric = Metric(WaveformKind.Ramp);
        var state = WaveformSampler.CreateState(metric);

        WaveformSampler.Next(metric, state, 0).Should().BeApproximately(10, 1e-9);
    }

    [Test]
    public void Next_Ramp_JustBeforePeriod_ApproachesMax()
    {
        var metric = Metric(WaveformKind.Ramp);
        var state = WaveformSampler.CreateState(metric);

        WaveformSampler.Next(metric, state, 59.94).Should().BeApproximately(29.98, 0.01);
    }

    [Test]
    public void Next_Ramp_AtFullPeriod_ResetsToMin()
    {
        var metric = Metric(WaveformKind.Ramp);
        var state = WaveformSampler.CreateState(metric);

        WaveformSampler.Next(metric, state, 60).Should().BeApproximately(10, 1e-9);
    }

    [Test]
    public void Next_Ramp_StaysWithinBounds()
    {
        var metric = Metric(WaveformKind.Ramp);
        var state = WaveformSampler.CreateState(metric);

        for (var t = 0.0; t <= 180.0; t += 0.25)
        {
            var value = WaveformSampler.Next(metric, state, t);
            value.Should().BeInRange(10, 30);
        }
    }

    [Test]
    public void Next_RandomWalk_StaysWithinBounds()
    {
        var metric = Metric(WaveformKind.RandomWalk, stepAmplitude: 5);
        var state = WaveformSampler.CreateState(metric);

        for (var tick = 0; tick < 1000; tick++)
        {
            var value = WaveformSampler.Next(metric, state, tick);
            value.Should().BeInRange(10, 30);
        }
    }

    [Test]
    public void Next_RandomWalk_SameMetric_ProducesIdenticalSequence()
    {
        var metric = Metric(WaveformKind.RandomWalk);
        var stateA = WaveformSampler.CreateState(metric);
        var stateB = WaveformSampler.CreateState(metric);

        var sequenceA = Enumerable.Range(0, 100).Select(i => WaveformSampler.Next(metric, stateA, i)).ToList();
        var sequenceB = Enumerable.Range(0, 100).Select(i => WaveformSampler.Next(metric, stateB, i)).ToList();

        sequenceA.Should().Equal(sequenceB);
    }

    [Test]
    public void Next_RandomWalk_DistinctMetricIds_ProduceDifferentSequences()
    {
        var metricA = Metric(WaveformKind.RandomWalk);
        var metricB = Metric(WaveformKind.RandomWalk);
        var stateA = WaveformSampler.CreateState(metricA);
        var stateB = WaveformSampler.CreateState(metricB);

        var sequenceA = Enumerable.Range(0, 50).Select(i => WaveformSampler.Next(metricA, stateA, i)).ToList();
        var sequenceB = Enumerable.Range(0, 50).Select(i => WaveformSampler.Next(metricB, stateB, i)).ToList();

        sequenceA.Should().NotEqual(sequenceB);
    }

    [Test]
    public void Next_Constant_ReturnsConstantValueAtAnyTime()
    {
        var metric = Metric(WaveformKind.Constant, constantValue: 42.5);
        var state = WaveformSampler.CreateState(metric);

        WaveformSampler.Next(metric, state, 0).Should().Be(42.5);
        WaveformSampler.Next(metric, state, 17.3).Should().Be(42.5);
        WaveformSampler.Next(metric, state, 9999).Should().Be(42.5);
    }

    [Test]
    public void Next_FixedBooleanTrue_ReturnsOne()
    {
        var metric = Metric(WaveformKind.FixedBoolean, MetricValueType.Boolean, booleanValue: true);
        var state = WaveformSampler.CreateState(metric);

        WaveformSampler.Next(metric, state, 5).Should().Be(1);
    }

    [Test]
    public void Next_FixedBooleanFalse_ReturnsZero()
    {
        var metric = Metric(WaveformKind.FixedBoolean, MetricValueType.Boolean, booleanValue: false);
        var state = WaveformSampler.CreateState(metric);

        WaveformSampler.Next(metric, state, 5).Should().Be(0);
    }

    [Test]
    public void Next_Toggle_FirstHalfPeriod_ReturnsTrue()
    {
        var metric = Metric(WaveformKind.Toggle, MetricValueType.Boolean, periodSeconds: 60);
        var state = WaveformSampler.CreateState(metric);

        WaveformSampler.Next(metric, state, 0).Should().Be(1);
        WaveformSampler.Next(metric, state, 29.9).Should().Be(1);
    }

    [Test]
    public void Next_Toggle_SecondHalfPeriod_ReturnsFalse()
    {
        var metric = Metric(WaveformKind.Toggle, MetricValueType.Boolean, periodSeconds: 60);
        var state = WaveformSampler.CreateState(metric);

        WaveformSampler.Next(metric, state, 30).Should().Be(0);
        WaveformSampler.Next(metric, state, 59.9).Should().Be(0);
    }

    [Test]
    public void Next_Toggle_AfterFullPeriod_FlipsBackToTrue()
    {
        var metric = Metric(WaveformKind.Toggle, MetricValueType.Boolean, periodSeconds: 60);
        var state = WaveformSampler.CreateState(metric);

        WaveformSampler.Next(metric, state, 75).Should().Be(1);
    }

    [Test]
    public void Next_RandomBoolean_ProbabilityZero_AlwaysFalse()
    {
        var metric = Metric(WaveformKind.RandomBoolean, MetricValueType.Boolean, trueProbability: 0);
        var state = WaveformSampler.CreateState(metric);

        for (var tick = 0; tick < 100; tick++)
            WaveformSampler.Next(metric, state, tick).Should().Be(0);
    }

    [Test]
    public void Next_RandomBoolean_ProbabilityOne_AlwaysTrue()
    {
        var metric = Metric(WaveformKind.RandomBoolean, MetricValueType.Boolean, trueProbability: 1);
        var state = WaveformSampler.CreateState(metric);

        for (var tick = 0; tick < 100; tick++)
            WaveformSampler.Next(metric, state, tick).Should().Be(1);
    }

    [Test]
    public void Next_Int64Metric_RoundsToNearestInteger()
    {
        var metric = Metric(WaveformKind.Sine, MetricValueType.Int64, min: 0, max: 10);
        var state = WaveformSampler.CreateState(metric);

        for (var t = 0.0; t <= 60.0; t += 1.3)
        {
            var value = WaveformSampler.Next(metric, state, t);
            value.Should().Be(Math.Round(value));
        }
    }

    [Test]
    public void PreviewSamples_DefaultCount_Returns64Samples()
    {
        var metric = Metric(WaveformKind.Sine);

        WaveformSampler.PreviewSamples(metric).Should().HaveCount(64);
    }

    [Test]
    public void PreviewSamples_Sine_CoversTwoFullPeriods()
    {
        var metric = Metric(WaveformKind.Sine);

        var samples = WaveformSampler.PreviewSamples(metric);

        samples[0].Should().BeApproximately(20, 1e-9);
        samples[^1].Should().BeApproximately(20, 1e-6);
        samples.Max().Should().BeGreaterThan(29.9);
        samples.Min().Should().BeLessThan(10.1);
    }

    [Test]
    public void PreviewSamples_Toggle_ProducesOnlyStepValues()
    {
        var metric = Metric(WaveformKind.Toggle, MetricValueType.Boolean);

        var samples = WaveformSampler.PreviewSamples(metric);

        samples.Should().OnlyContain(v => v == 0 || v == 1);
        samples.Should().Contain(0);
        samples.Should().Contain(1);
    }

    [Test]
    public void PreviewSamples_RandomWalk_IsDeterministicAcrossCalls()
    {
        var metric = Metric(WaveformKind.RandomWalk);

        var first = WaveformSampler.PreviewSamples(metric);
        var second = WaveformSampler.PreviewSamples(metric);

        first.Should().Equal(second);
    }

    [Test]
    public void PreviewSamples_Constant_AllValuesEqual()
    {
        var metric = Metric(WaveformKind.Constant, constantValue: 7.25);

        var samples = WaveformSampler.PreviewSamples(metric);

        samples.Should().OnlyContain(v => v == 7.25);
    }
}
