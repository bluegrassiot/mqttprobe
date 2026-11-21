using MqttProbe.Core.Services.Sparkplug;
using MudBlazor;

namespace MqttProbe.UI.Components.Sparkplug;

public partial class SparkplugNodesView
{
    private async Task WriteMetricAsync(SparkplugMetricWriteTarget target)
    {
        var metric = target.Metric;
        var parameters = new DialogParameters<WriteMetricDialog>
        {
            { x => x.Topic, target.CommandTopic },
            { x => x.MetricName, metric.Name },
            { x => x.DataType, metric.DataType },
            { x => x.CurrentValue, metric.Value },
            { x => x.InitialValue, metric.EditValue ?? metric.Value }
        };
        var dialog = await DialogService.ShowAsync<WriteMetricDialog>(
            "Write metric", parameters,
            new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true });
        var result = await dialog.Result;
        if (result is not { Canceled: false, Data: string value }) return;

        var commandResult = target.DeviceId is null
            ? await CommandService.WriteNodeMetricAsync(target.GroupId, target.NodeId, metric.Name, value)
            : await CommandService.WriteDeviceMetricAsync(
                target.GroupId, target.NodeId, target.DeviceId, metric.Name, value);

        switch (commandResult)
        {
            case SparkplugCommandResult.Published:
                Snackbar.Add($"Wrote {metric.Name} on {target.Path}.", Severity.Info);
                break;
            case SparkplugCommandResult.NotAllowed:
                Snackbar.Add("Metric writes are not allowed. Enable Allow metric writes in Settings, Sparkplug.", Severity.Warning);
                break;
            case SparkplugCommandResult.TargetNotFound:
                Snackbar.Add($"Write not sent: {metric.Name} is not in the current birth certificate for {target.Path}.", Severity.Warning);
                break;
            case SparkplugCommandResult.InvalidValue:
                Snackbar.Add($"Write not sent: the value is not valid for {metric.Name} ({metric.DataType}).", Severity.Warning);
                break;
            default:
                Snackbar.Add($"Write to {metric.Name} on {target.Path} was not sent. Check the broker connection.", Severity.Warning);
                break;
        }
    }
}
