using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Extensions.ManagedClient;
using MqttProbe.Components.Layout;
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

namespace MqttProbe;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();

        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts => { fonts.AddFont("Inter-Variable.ttf", "Inter"); });

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
        builder.Services.AddMauiBlazorWebView();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        // MAUI has one local app session, so MQTT and UI state services are shared across pages.
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
        builder.Services.AddSingleton<IAppInfoService, AppInfoService>();
#if WINDOWS
        builder.Services.AddSingleton<IUpdateService, MqttProbe.WinUI.VelopackUpdateService>();
#else
        builder.Services.AddSingleton<IUpdateService, NoOpUpdateService>();
#endif
        builder.Services.AddAuthorizationCore();
        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddScoped<AuthenticationStateProvider, UnauthenticatedStateProvider>();
        builder.Services.AddTransient<MainPage>();

        var secretStorage = new MauiSecretStorage();
        builder.Services.AddSingleton<ISecretStorage>(secretStorage);

        var configDir = Path.Combine(FileSystem.Current.AppDataDirectory, "config");
#if WINDOWS
        // v1.0.1 and earlier shipped with the MAUI template's placeholder publisher
        // ("User Name"), so existing users' config lives under that directory.
        var legacyConfigDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "User Name", "com.bluegrassiot.mqttprobe", "Data", "config");
        ConfigMigrator.MigrateIfNeeded(legacyConfigDir, configDir);
#endif
        var configPath = Path.Combine(configDir, "appsettings.json");
        var isMobile = DeviceInfo.Idiom == DeviceIdiom.Phone || DeviceInfo.Idiom == DeviceIdiom.Tablet;
        var settingsStore = new SettingsStore(configPath, isMobile, null);
        builder.Services.AddSingleton<ISettingsStore>(settingsStore);

        builder.Services.AddScoped<IClipboardService, MauiClipboardService>();
        builder.Services.AddSingleton<IJsonFieldExtractor, JsonFieldExtractor>();
        builder.Services.AddSingleton<IChartFieldRegistry, ChartFieldRegistry>();
        builder.Services.AddScoped<IChartDataService, ChartDataService>();
        builder.Services.AddSingleton<ISparkplugTopologyService, SparkplugTopologyService>();
        builder.Services.AddSingleton<IPayloadDecoder, PayloadDecoder>();
        builder.Services.AddScoped<IThemes, Themes>();

        return builder.Build();
    }
}
