using Microsoft.Extensions.Logging;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Security;

namespace MqttProbe;

public partial class App
{
    private readonly ISettingsStore _settingsStore;
    private readonly ISecretStorage _secretStorage;
    private readonly ILogger<App> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly ICertificateAssetStore _certStore;
    private readonly ICertificateEnvelopeKeyStore _envelopeKeyStore;

    public App(ISettingsStore settingsStore, ISecretStorage secretStorage,
        ILogger<App> logger, IServiceProvider serviceProvider,
        ICertificateAssetStore certStore, ICertificateEnvelopeKeyStore envelopeKeyStore)
    {
        InitializeComponent();
        _settingsStore = settingsStore;
        _secretStorage = secretStorage;
        _logger = logger;
        _serviceProvider = serviceProvider;
        _certStore = certStore;
        _envelopeKeyStore = envelopeKeyStore;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(CreateLoadingPage());

#if WINDOWS
        window.Created += (_, _) =>
        {
            if (window.Handler?.PlatformView is Microsoft.UI.Xaml.Window nativeWindow)
            {
                var handle = WinRT.Interop.WindowNative.GetWindowHandle(nativeWindow);
                var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle);
                var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
                (appWindow.Presenter as Microsoft.UI.Windowing.OverlappedPresenter)?.Maximize();

                appWindow.TitleBar.ExtendsContentIntoTitleBar = true;

                if (nativeWindow.Content is Microsoft.UI.Xaml.Controls.Panel rootPanel)
                {
                    rootPanel.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                        Microsoft.UI.ColorHelper.FromArgb(255, 0x1E, 0x29, 0x3B));
                }
                appWindow.TitleBar.BackgroundColor = global::Windows.UI.Color.FromArgb(255, 0x1E, 0x29, 0x3B);
                appWindow.TitleBar.ForegroundColor = global::Windows.UI.Color.FromArgb(255, 0xF1, 0xF5, 0xF9);
                appWindow.TitleBar.InactiveBackgroundColor = global::Windows.UI.Color.FromArgb(255, 0x1E, 0x29, 0x3B);
                appWindow.TitleBar.InactiveForegroundColor = global::Windows.UI.Color.FromArgb(255, 0x64, 0x74, 0x8B);
                appWindow.TitleBar.ButtonBackgroundColor = global::Windows.UI.Color.FromArgb(255, 0x1E, 0x29, 0x3B);
                appWindow.TitleBar.ButtonForegroundColor = global::Windows.UI.Color.FromArgb(255, 0x94, 0xA3, 0xB8);
                appWindow.TitleBar.ButtonHoverBackgroundColor = global::Windows.UI.Color.FromArgb(255, 0x29, 0x35, 0x48);
                appWindow.TitleBar.ButtonHoverForegroundColor = global::Windows.UI.Color.FromArgb(255, 0xF9, 0x73, 0x16);
                appWindow.TitleBar.ButtonPressedBackgroundColor = global::Windows.UI.Color.FromArgb(255, 0x34, 0x41, 0x56);
                appWindow.TitleBar.ButtonPressedForegroundColor = global::Windows.UI.Color.FromArgb(255, 0xF1, 0xF5, 0xF9);
                appWindow.TitleBar.ButtonInactiveBackgroundColor = global::Windows.UI.Color.FromArgb(255, 0x1E, 0x29, 0x3B);
                appWindow.TitleBar.ButtonInactiveForegroundColor = global::Windows.UI.Color.FromArgb(255, 0x64, 0x74, 0x8B);
            }
        };
#endif

        _ = InitializeStartupAsync(window);
        return window;
    }

    private async Task InitializeStartupAsync(Window window)
    {
        try
        {
            await MainThread.InvokeOnMainThreadAsync(() => window.Page = CreateLoadingPage());
            await _settingsStore.LoadAsync(_secretStorage, _certStore, _envelopeKeyStore);

            var root = await ResolveInitialPageAsync();
            await MainThread.InvokeOnMainThreadAsync(() => window.Page = root);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MAUI startup failed.");
            await MainThread.InvokeOnMainThreadAsync(() => window.Page = CreateStartupErrorPage(window));
        }
    }

    private Task<Page> ResolveInitialPageAsync()
        => Task.FromResult<Page>(_serviceProvider.GetRequiredService<MainPage>());

    private static Page CreateLoadingPage() =>
        new ContentPage
        {
            BackgroundColor = Color.FromArgb("#0F172A"),
            Content = new VerticalStackLayout
            {
                Padding = 32,
                Spacing = 12,
                VerticalOptions = LayoutOptions.Center,
                Children =
                {
                    new ActivityIndicator
                    {
                        IsRunning = true,
                        Color = Color.FromArgb("#F97316"),
                        HorizontalOptions = LayoutOptions.Center
                    },
                    new Label
                    {
                        Text = "Starting MQTTProbe...",
                        FontSize = 18,
                        HorizontalTextAlignment = TextAlignment.Center,
                        TextColor = Colors.White
                    }
                }
            }
        };

    private Page CreateStartupErrorPage(Window window)
    {
        var retryButton = new Button
        {
            Text = "Retry",
            BackgroundColor = Color.FromArgb("#F97316"),
            TextColor = Color.FromArgb("#0F172A")
        };
        retryButton.Clicked += (_, _) => _ = InitializeStartupAsync(window);

        return new ContentPage
        {
            BackgroundColor = Color.FromArgb("#0F172A"),
            Content = new VerticalStackLayout
            {
                Padding = 32,
                Spacing = 16,
                VerticalOptions = LayoutOptions.Center,
                Children =
                {
                    new Label
                    {
                        Text = "Startup failed.",
                        FontSize = 20,
                        FontAttributes = FontAttributes.Bold,
                        HorizontalTextAlignment = TextAlignment.Center,
                        TextColor = Colors.White
                    },
                    new Label
                    {
                        Text = "MQTTProbe could not load its local settings. Check the logs and try again.",
                        FontSize = 14,
                        HorizontalTextAlignment = TextAlignment.Center,
                        TextColor = Color.FromArgb("#CBD5E1")
                    },
                    retryButton
                }
            }
        };
    }
}
