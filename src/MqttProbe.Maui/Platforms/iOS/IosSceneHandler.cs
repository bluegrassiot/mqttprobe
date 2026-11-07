using UIKit;

namespace MqttProbe;

[Foundation.Register("SceneDelegate")]
public class IosSceneHandler : Microsoft.Maui.MauiUISceneDelegate
{
#pragma warning disable CA1822 // UIKit sends this selector to the scene delegate instance
    [Foundation.Export("supportedInterfaceOrientationsForWindowScene:")]
    public UIInterfaceOrientationMask GetSupportedInterfaceOrientations(UIWindowScene windowScene)
    {
        return IosOrientationPolicy.ForWindow(
            windowScene.KeyWindow ?? (windowScene.Windows.Length > 0 ? windowScene.Windows[0] : null),
            windowScene.TraitCollection.UserInterfaceIdiom);
    }
#pragma warning restore CA1822
}
