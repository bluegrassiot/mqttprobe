using MqttProbe.Models.Emulation;

namespace MqttProbe.Services.Emulation;

public sealed class WaveformState
{
    internal WaveformState(Random rng, double current)
    {
        Rng = rng;
        Current = current;
    }

    internal Random Rng { get; }
    internal double Current { get; set; }
}

public static class WaveformSampler
{
    public static WaveformState CreateState(EmulatorMetricConfig metric) =>
        // Seeded from the metric Id so previews and runtime walks are reproducible per metric.
        new(new Random(metric.Id.GetHashCode()), (metric.Min + metric.Max) / 2.0);

    public static double Next(EmulatorMetricConfig metric, WaveformState state, double tSeconds)
    {
        var value = metric.Waveform switch
        {
            WaveformKind.Sine => Sine(metric, tSeconds),
            WaveformKind.Ramp => Ramp(metric, tSeconds),
            WaveformKind.RandomWalk => RandomWalk(metric, state),
            WaveformKind.Constant => metric.ConstantValue,
            WaveformKind.FixedBoolean => metric.BooleanValue ? 1.0 : 0.0,
            WaveformKind.Toggle => Toggle(metric, tSeconds),
            WaveformKind.RandomBoolean => state.Rng.NextDouble() < metric.TrueProbability ? 1.0 : 0.0,
            _ => 0.0
        };
        return metric.ValueType == MetricValueType.Int64 ? Math.Round(value) : value;
    }

    public static double[] PreviewSamples(EmulatorMetricConfig metric, int count = 64)
    {
        // One loop covers both families: time-based kinds read t (two full periods),
        // tick-based kinds advance the seeded state once per sample and ignore t.
        var samples = new double[count];
        var state = CreateState(metric);
        for (var i = 0; i < count; i++)
        {
            var t = count > 1 ? 2.0 * metric.PeriodSeconds * i / (count - 1) : 0.0;
            samples[i] = Next(metric, state, t);
        }

        return samples;
    }

    private static double Sine(EmulatorMetricConfig metric, double tSeconds)
    {
        var mid = (metric.Min + metric.Max) / 2.0;
        if (metric.PeriodSeconds <= 0) return mid;
        var amp = (metric.Max - metric.Min) / 2.0;
        return mid + amp * Math.Sin(2.0 * Math.PI * tSeconds / metric.PeriodSeconds);
    }

    private static double Ramp(EmulatorMetricConfig metric, double tSeconds)
    {
        if (metric.PeriodSeconds <= 0) return metric.Min;
        var cycles = tSeconds / metric.PeriodSeconds;
        var fraction = cycles - Math.Floor(cycles);
        return metric.Min + (metric.Max - metric.Min) * fraction;
    }

    private static double RandomWalk(EmulatorMetricConfig metric, WaveformState state)
    {
        var step = (state.Rng.NextDouble() * 2.0 - 1.0) * metric.StepAmplitude;
        state.Current = Math.Clamp(state.Current + step, metric.Min, metric.Max);
        return state.Current;
    }

    private static double Toggle(EmulatorMetricConfig metric, double tSeconds)
    {
        if (metric.PeriodSeconds <= 0) return 1.0;
        var phase = tSeconds % metric.PeriodSeconds;
        return phase < metric.PeriodSeconds / 2.0 ? 1.0 : 0.0;
    }
}
