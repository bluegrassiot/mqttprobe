using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Extensions.ManagedClient;
using MqttProbe.Components.Layout;
using MqttProbe.Desktop.Interop;
using MqttProbe.Desktop.Services;
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
using Velopack;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        VelopackApp.Build().Run();

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
        builder.Services.AddSingleton<IEmulationService>(sp =>
            new EmulationService(
                sp.GetRequiredService<ISettingsStore>(),
                sp.GetRequiredService<ISparkplugNodeFactory>(),
                sp.GetRequiredService<ISessionState>(),
                sp.GetRequiredService<IManagedMqttClient>(),
                sp.GetRequiredService<IUxMetricsService>(),
                sp.GetRequiredService<ICertificateAssetStore>(),
                sp.GetRequiredService<ICertificateSessionQuarantine>(),
                sp.GetRequiredService<ILogger<EmulationService>>(),
                sp.GetRequiredService<IAppHealthMetricsCollector>()));
        builder.Services.AddSingleton<IMessageStoreManager, MessageStoreManager>();
        builder.Services.AddScoped<ISubscriptionManager, SubscriptionManager>();
        builder.Services.AddScoped<IBrokerStateResetCoordinator, BrokerStateResetCoordinator>();
        builder.Services.AddSingleton<IMqttOptionsBuilder>(sp =>
            new MqttOptionsBuilder(sp.GetRequiredService<ICertificateAssetStore>()));
        builder.Services.AddSingleton<IConnectionSessionLifecycle, ConnectionSessionLifecycle>();
        builder.Services.AddSingleton<ICertificateSessionQuarantine, CertificateSessionQuarantine>();
        builder.Services.AddSingleton<IAppHealthMetricsCollector, AppHealthMetricsCollector>();
        builder.Services.AddSingleton<IUxMetricsService, UxMetricsService>();
        builder.Services.AddSingleton<ISparkplugNodeFactory, SparkplugNodeFactory>();
        builder.Services.AddSingleton<IAppInfoService, DesktopAppInfoService>();
        builder.Services.AddSingleton<IUpdateService, DesktopVelopackUpdateService>();
        builder.Services.AddAuthorizationCore();
        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddScoped<AuthenticationStateProvider, DesktopUnauthenticatedStateProvider>();

        var secretStorage = new DesktopSecretStorage();
        builder.Services.AddSingleton<ISecretStorage>(secretStorage);

        builder.Services.AddSingleton<IPhotinoWindowAccessor, PhotinoWindowAccessor>();
        builder.Services.AddSingleton<ICertificateEnvelopeKeyStore>(sp =>
            new DesktopCertificateEnvelopeKeyStore(sp.GetRequiredService<ISecretStorage>()));
        builder.Services.AddSingleton<IFileProtector, DefaultFileProtector>();

        var configDir = Path.Combine(
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
            "mqttprobe");
        Directory.CreateDirectory(configDir);
        var configPath = Path.Combine(configDir, "appsettings.json");
        builder.Services.AddSingleton<ISettingsStore>(sp =>
            new SettingsStore(configPath, isMobile: false,
                logger: sp.GetRequiredService<ILogger<SettingsStore>>()));

        builder.Services.AddSingleton<ICertificateAssetStore>(sp =>
        {
            var store = new CertificateAssetStore(
                sp.GetRequiredService<ICertificateEnvelopeKeyStore>(),
                configDir,
                sp.GetRequiredService<ILogger<CertificateAssetStore>>());
            return store;
        });
        builder.Services.AddSingleton<ICertificateSessionQuarantine, CertificateSessionQuarantine>();
        builder.Services.AddSingleton<ICertificateFilePicker>(sp =>
            new DesktopCertificateFilePicker(sp.GetRequiredService<IPhotinoWindowAccessor>()));
        builder.Services.AddSingleton<ICertificateInputCapability, DesktopCertificateInputCapability>();
        builder.Services.AddSingleton<IConnectionSessionLifecycle, ConnectionSessionLifecycle>();

        builder.Services.AddScoped<IClipboardService, DesktopClipboardService>();
        builder.Services.AddSingleton<IJsonFieldExtractor, JsonFieldExtractor>();
        builder.Services.AddSingleton<IChartFieldRegistry, ChartFieldRegistry>();
        builder.Services.AddSingleton<IPayloadDecoder, PayloadDecoder>();
        builder.Services.AddScoped<IChartDataService, ChartDataService>();
        builder.Services.AddSingleton<ISparkplugTopologyService, SparkplugTopologyService>();
        builder.Services.AddScoped<IThemes, Themes>();

        builder.RootComponents.Add<MqttProbe.Desktop.Main>("app");

        var app = builder.Build();

        app.Services.GetRequiredService<IPhotinoWindowAccessor>().Window = app.MainWindow;
        var resolvedSettingsStore = app.Services.GetRequiredService<ISettingsStore>();
        resolvedSettingsStore.LoadAsync(secretStorage, app.Services.GetService<ICertificateAssetStore>(), app.Services.GetService<ICertificateEnvelopeKeyStore>()).GetAwaiter().GetResult();

        // .ico for the Win32 window/titlebar; .png for the Linux WM/dock.
        var iconFile = OperatingSystem.IsWindows() ? "icon.ico" : "icon.png";
        app.MainWindow
            .SetTitle("")
            // Photino's Log() is binary: it prints unless LogVerbosity <= 0. Levels 1 and 2
            // behave identically (both flood stdout with SendWebMessage/RenderBatch blobs that
            // bury app logs), so 0 is the only value that silences it.
            .SetLogVerbosity(0)
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
