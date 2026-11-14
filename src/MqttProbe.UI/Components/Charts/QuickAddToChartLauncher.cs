using MudBlazor;

namespace MqttProbe.UI.Components.Charts;

public static class QuickAddToChartLauncher
{
    public static async Task ShowAsync(
        IDialogService dialogService,
        ISnackbar snackbar,
        string topic,
        string jsonPath,
        string seriesName)
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
