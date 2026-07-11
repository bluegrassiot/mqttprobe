using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Extensions.ManagedClient;
using MqttProbe.Components.Layout;
using MqttProbe.Desktop.Interop;
using MqttProbe.Services;
using MqttProbe.Services.Chart;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Emulation;
using MqttProbe.Services.Metrics;
using MqttProbe.Services.Mqtt;
using MqttProbe.Services.Platform;
using MqttProbe.Services.Security;
using MqttProbe.Services.Sparkplug;
using MudBlazor;
using MudBlazor.Services;
using Photino.Blazor;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var builder = PhotinoBlazorAppBuilder.CreateDefault(args);

        builder.Services.AddMudServices(config =>
        {
            config.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.TopCenter;
            config.SnackbarConfiguration.RequireInteraction = false;
            config.SnackbarConfiguration.PreventDuplicates = true;
            config.SnackbarConfiguration.NewestOnTop = false;
            config.SnackbarConfiguration.ShowCloseIcon = true;
            config.SnackbarConfiguration.VisibleStateDuration = 3000;
            config.SnackbarConfiguration.HideTransitionDuration = 500;
            config.SnackbarConfiguration.ShowTransitionDuration = 500;
            config.SnackbarConfiguration.SnackbarVariant = Variant.Filled;
        });

        builder.Services.AddLogging(logging =>
        {
            logging.AddConsole();
            logging.AddDebug();
        });

        // Singleton DI model matching MAUI host (one native session).
        var client = new MqttFactory().CreateManagedMqttClient();
        builder.Services.AddSingleton(client);
        builder.Services.AddSingleton<ISessionState, SessionState>();
        builder.Services.AddSingleton<IEmulationService, EmulationService>();
        builder.Services.AddSingleton<IMessageStoreManager, MessageStoreManager>();
        builder.Services.AddScoped<ISubscriptionManager, SubscriptionManager>();
        builder.Services.AddScoped<IBrokerStateResetCoordinator, BrokerStateResetCoordinator>();
        builder.Services.AddSingleton<IMqttOptionsBuilder, MqttOptionsBuilder>();
        builder.Services.AddSingleton<IUxMetricsService, UxMetricsService>();
        builder.Services.AddSingleton<ISparkplugNodeFactory, SparkplugNodeFactory>();
        builder.Services.AddSingleton<IAppInfoService, DesktopAppInfoService>();
        builder.Services.AddAuthorizationCore();
        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddScoped<AuthenticationStateProvider, DesktopUnauthenticatedStateProvider>();

        var secretStorage = new DesktopSecretStorage();
        builder.Services.AddSingleton<ISecretStorage>(secretStorage);

        var configDir = Path.Combine(
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
            "mqttprobe");
        Directory.CreateDirectory(configDir);
        var configPath = Path.Combine(configDir, "appsettings.json");
        var settingsStore = new SettingsStore(configPath, isMobile: false, logger: null);
        builder.Services.AddSingleton<ISettingsStore>(settingsStore);

        builder.Services.AddScoped<IClipboardService, DesktopClipboardService>();
        builder.Services.AddSingleton<IJsonFieldExtractor, JsonFieldExtractor>();
        builder.Services.AddSingleton<IChartFieldRegistry, ChartFieldRegistry>();
        builder.Services.AddSingleton<IPayloadDecoder, PayloadDecoder>();
        builder.Services.AddScoped<IChartDataService, ChartDataService>();
        builder.Services.AddSingleton<ISparkplugTopologyService, SparkplugTopologyService>();
        builder.Services.AddScoped<IThemes, Themes>();

        builder.RootComponents.Add<MqttProbe.Desktop.Main>("app");

        var app = builder.Build();

        settingsStore.LoadAsync(secretStorage).GetAwaiter().GetResult();

        // .ico for the Win32 window/titlebar; .png for the Linux WM/dock.
        var iconFile = OperatingSystem.IsWindows() ? "icon.ico" : "icon.png";
        app.MainWindow
            .SetTitle("")
            .SetWidth(1280)
            .SetHeight(800)
            .SetMaximized(true)
            .SetIconFile(Path.Combine(AppContext.BaseDirectory, "Assets", iconFile))
            .RegisterWindowCreatedHandler((_, _) =>
            {
                if (OperatingSystem.IsWindows())
                    WindowsTitleBar.ApplyBrandTint(app.MainWindow.WindowHandle);
            });

        app.Run();
    }
}
