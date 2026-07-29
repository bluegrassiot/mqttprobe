using Foundation;

namespace MqttProbe;

// CA1711: "AppDelegate" is the required name for the iOS app delegate type
// (MAUI/UIKit convention referenced by [Register] and the native runtime);
// renaming it is not an option, not just an API-surface preference.
#pragma warning disable CA1711 // Identifiers should not have incorrect suffix
[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
#pragma warning restore CA1711
{
    protected override MauiApp CreateMauiApp()
    {
        return MauiProgram.CreateMauiApp();
    }
}
