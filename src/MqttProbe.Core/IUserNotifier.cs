namespace MqttProbe.Core;

public enum UserNotificationSeverity
{
    Info,
    Success,
    Warning,
    Error
}

public sealed record UserNotification(UserNotificationSeverity Severity, string Message);

public interface IUserNotifier
{
    public void Notify(UserNotification notification);
}
