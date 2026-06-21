using Android.App;
using Android.Content.PM;
using Android.OS;

namespace MqttProbe;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode |
                           ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density,
                           ScreenOrientation = ScreenOrientation.FullSensor)]
public class MainActivity : MauiAppCompatActivity
{
}
