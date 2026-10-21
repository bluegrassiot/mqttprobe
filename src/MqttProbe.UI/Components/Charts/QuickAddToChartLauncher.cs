using MqttProbe.Core.Models.Chart;
using MudBlazor;

namespace MqttProbe.UI.Components.Charts;

// One place that turns a field selection into the quick-add dialog and reports the
// outcome, so every surface that offers "add to chart" behaves the same.
public static class QuickAddToChartLauncher
{
    public static async Task OpenAsync(
        IDialogService dialogService,
        ISnackbar snackbar,
        string topic,
        ChartFieldSelection selection)
    {
        var dialog = await dialogService.ShowAsync<QuickAddToChartDialog>(
            "Chart Field", BuildParameters(topic, selection),
            new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true });

        var result = await dialog.Result;
        if (result is { Canceled: false, Data: string message })
            snackbar.Add(message, Severity.Success);
    }

    public static DialogParameters<QuickAddToChartDialog> BuildParameters(
        string topic,
        ChartFieldSelection selection) =>
        new()
        {
            { x => x.Topic, topic },
            { x => x.JsonPath, selection.JsonPath },
            { x => x.SeriesName, SeriesLabel(selection) }
        };

    public static string SeriesLabel(ChartFieldSelection selection) =>
        string.IsNullOrWhiteSpace(selection.SeriesName)
            ? ShortLabel(selection.JsonPath)
            : selection.SeriesName;

    public static string ShortLabel(string jsonPath)
    {
        var dot = jsonPath.LastIndexOf('.');
        return dot >= 0 ? jsonPath[(dot + 1)..] : jsonPath;
    }
}
