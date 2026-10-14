namespace MqttProbe.UI.Components.Layout;

public partial class ConnectionRequiredPanel
{
    // Virtual so tests can exercise the iOS branch from any host OS.
    protected virtual bool IsIosHost => OperatingSystem.IsIOS();
}
