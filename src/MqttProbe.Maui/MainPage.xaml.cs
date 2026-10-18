namespace MqttProbe;

public partial class MainPage
{
    public MainPage()
    {
        InitializeComponent();
    }

    private static void OnBlazorWebViewInitialized(object? sender, Microsoft.AspNetCore.Components.WebView.BlazorWebViewInitializedEventArgs e)
    {
#if WINDOWS
        e.WebView.DefaultBackgroundColor = Windows.UI.Color.FromArgb(255, 30, 41, 59);
#elif IOS
        var backgroundColor = UIKit.UIColor.FromRGB(30, 41, 59);
        e.WebView.Opaque = false;
        e.WebView.BackgroundColor = backgroundColor;
        e.WebView.ScrollView.BackgroundColor = backgroundColor;
#endif
    }
}
