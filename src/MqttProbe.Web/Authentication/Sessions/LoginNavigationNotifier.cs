namespace MqttProbe.Web.Authentication;

public interface ILoginNavigationNotifier
{
    public event Action? ForceLoginRequested;

    public void NotifyForceLogin();
}

public sealed class LoginNavigationNotifier : ILoginNavigationNotifier
{
    public event Action? ForceLoginRequested;

    public void NotifyForceLogin() => ForceLoginRequested?.Invoke();
}
