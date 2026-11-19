using System.Diagnostics;
using Microsoft.AspNetCore.Components;
using MqttProbe.Core.Services.Metrics;

namespace MqttProbe.UI.Components.Charts;

public partial class ChartCard
{
    [Parameter] public IUxMetricsService? Metrics { get; set; }

    private Stopwatch? BeginRendererCall() => Metrics is null ? null : Stopwatch.StartNew();

    private void RecordRendererCall(string renderer, TimeSpan? elapsed, bool failed)
    {
        if (elapsed is { } duration && Metrics is { } metrics)
            metrics.RecordChartRendererCall(renderer, duration, failed);
    }
}
