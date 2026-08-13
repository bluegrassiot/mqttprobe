using MqttProbe.Core;
using MudBlazor;

namespace MqttProbe.UI.Services.Platform;

internal sealed class MudBlazorUserNotifier(ISnackbar snackbar) : IUserNotifier
{
    public void Notify(UserNotification notification)
    {
        var severity = notification.Severity switch
        {
            UserNotificationSeverity.Info => Severity.Info,
            UserNotificationSeverity.Success => Severity.Success,
            UserNotificationSeverity.Warning => Severity.Warning,
            UserNotificationSeverity.Error => Severity.Error,
            _ => Severity.Normal
        };

        snackbar.Add(notification.Message, severity);
    }
}
