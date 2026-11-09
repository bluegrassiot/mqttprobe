using Foundation;
using UIKit;

namespace MqttProbe;

// CA1711: "AppDelegate" is the required name for the iOS app delegate type
// UIKit resolves this type by name through the Register attribute, so renaming it
// breaks app startup. This is not merely an API-surface preference.
#pragma warning disable CA1711 // Identifiers should not have incorrect suffix
[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
#pragma warning restore CA1711
{
    protected override MauiApp CreateMauiApp()
    {
        return MauiProgram.CreateMauiApp();
    }

#pragma warning disable CA1822 // UIKit sends this selector to the delegate instance
    [Export("application:supportedInterfaceOrientationsForWindow:")]
    public UIInterfaceOrientationMask GetSupportedInterfaceOrientations(UIApplication application, UIWindow? window)
    {
        return IosOrientationPolicy.ForWindow(window);
    }
#pragma warning restore CA1822
}
