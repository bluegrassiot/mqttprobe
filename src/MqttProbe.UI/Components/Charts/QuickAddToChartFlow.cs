using MudBlazor;

namespace MqttProbe.UI.Components.Charts;

internal static class QuickAddToChartFlow
{
    internal static async Task OpenAsync(
        IDialogService dialogService,
        ISnackbar snackbar,
        string jsonPath,
        string seriesName,
        string topic)
    {
        var parameters = new DialogParameters<QuickAddToChartDialog>
        {
            { x => x.Topic, topic },
            { x => x.JsonPath, jsonPath },
            { x => x.SeriesName, seriesName }
        };
        var dialog = await dialogService.ShowAsync<QuickAddToChartDialog>(
            "Chart Field", parameters,
            new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true });
        var result = await dialog.Result;
        if (result is { Canceled: false, Data: string message })
            snackbar.Add(message, Severity.Success);
    }
}
