using MqttProbe.Models.Chart;

namespace MqttProbe.Services.Configuration;

internal sealed class ChartSettings(ISettingsDocument document) : IChartSettings
{
    public event Action<Guid>? ChartsChanged;

    public IReadOnlyList<ChartConfiguration> GetCharts(Guid connectionId) =>
        document.Config.ChartsByConnection.TryGetValue(connectionId, out var charts)
            ? charts
            : [];

    public async Task AddChartAsync(Guid connectionId, ChartConfiguration chart)
    {
        await document.MutateAndSaveAsync(config =>
        {
            if (!config.ChartsByConnection.TryGetValue(connectionId, out var charts))
            {
                charts = [];
                config.ChartsByConnection[connectionId] = charts;
            }

            charts.Add(chart);
        });

        ChartsChanged?.Invoke(connectionId);
    }

    public async Task UpdateChartAsync(Guid connectionId, ChartConfiguration chart)
    {
        await document.MutateAndSaveAsync(config =>
        {
            if (config.ChartsByConnection.TryGetValue(connectionId, out var charts))
            {
                var idx = charts.FindIndex(existing => existing.Id == chart.Id);
                if (idx >= 0) charts[idx] = chart;
            }
        });

        ChartsChanged?.Invoke(connectionId);
    }

    public async Task RemoveChartAsync(Guid connectionId, Guid chartId)
    {
        await document.MutateAndSaveAsync(config =>
        {
            if (config.ChartsByConnection.TryGetValue(connectionId, out var charts))
                charts.RemoveAll(chart => chart.Id == chartId);
        });

        ChartsChanged?.Invoke(connectionId);
    }
}
