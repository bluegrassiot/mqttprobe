using System.Globalization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Extensions.ManagedClient;
using MqttProbe.Components.Layout;
using MqttProbe.Maui.Services;
using MqttProbe.Models.Plugins;
using MqttProbe.Services;
using MqttProbe.Services.Chart;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Emulation;
using MqttProbe.Services.Metrics;
using MqttProbe.Services.Mqtt;
using MqttProbe.Services.Platform;
using MqttProbe.Services.Plugins;
using MqttProbe.Services.Plugins.Pipeline;
using MqttProbe.Services.Plugins.Registry;
using MqttProbe.Services.Security;
using MqttProbe.Services.Sparkplug;
using MudBlazor;
using MudBlazor.Services;

namespace MqttProbe;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
#if IOS
        CultureInfo.DefaultThreadCurrentCulture = new CultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("en-US");
#endif

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
                sp.GetRequiredService<PayloadPipeline>(),
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
        builder.Services.AddSingleton<IAppInfoService, AppInfoService>();
#if WINDOWS
        builder.Services.AddSingleton<IUpdateService, MqttProbe.WinUI.VelopackUpdateService>();
#elif MACCATALYST
        builder.Services.AddSingleton<IUpdateService, MacVelopackUpdateService>();
#else
        builder.Services.AddSingleton<IUpdateService, NoOpUpdateService>();
#endif
        builder.Services.AddAuthorizationCore();
        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddScoped<AuthenticationStateProvider, UnauthenticatedStateProvider>();
        builder.Services.AddTransient<MainPage>();

        var secretStorage = new MauiSecretStorage();
        builder.Services.AddSingleton<ISecretStorage>(secretStorage);

#if IOS
        builder.Services.AddSingleton<ICertificateEnvelopeKeyStore, IosCertificateEnvelopeKeyStore>();
        builder.Services.AddSingleton<IFileProtector>(new IosFileProtector());
#else
        builder.Services.AddSingleton<ICertificateEnvelopeKeyStore>(sp =>
            new MauiCertificateEnvelopeKeyStore(sp.GetRequiredService<ISecretStorage>()));
        builder.Services.AddSingleton<IFileProtector, DefaultFileProtector>();
#endif

#if IOS || MACCATALYST
        builder.Services.AddSingleton<ICertificateAssetStore>(sp =>
        {
            var baseStore = new CertificateAssetStore(
                sp.GetRequiredService<ICertificateEnvelopeKeyStore>(),
                FileSystem.Current.AppDataDirectory,
                sp.GetRequiredService<ILogger<CertificateAssetStore>>());
            return new MauiCertificateAssetStore(
                baseStore,
                baseStore,
                sp.GetRequiredService<ICertificateEnvelopeKeyStore>(),
                baseStore.CertificatesDirectory,
                sp.GetRequiredService<IFileProtector>(),
                sp.GetRequiredService<ILogger<MauiCertificateAssetStore>>());
        });
#else
        builder.Services.AddSingleton<ICertificateAssetStore>(sp =>
            new CertificateAssetStore(
                sp.GetRequiredService<ICertificateEnvelopeKeyStore>(),
                FileSystem.Current.AppDataDirectory,
                sp.GetRequiredService<ILogger<CertificateAssetStore>>()));
#endif
        builder.Services.AddSingleton<ICertificateFilePicker, MauiCertificateFilePicker>();
        builder.Services.AddSingleton<ICertificateInputCapability, MauiCertificateInputCapability>();

        var configDir = Path.Combine(FileSystem.Current.AppDataDirectory, "config");
#if WINDOWS
        var legacyConfigDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "User Name", "com.bluegrassiot.mqttprobe", "Data", "config");
        ConfigMigrator.MigrateIfNeeded(legacyConfigDir, configDir);
#endif
        var configPath = Path.Combine(configDir, "appsettings.json");
        var isMobile = DeviceInfo.Idiom == DeviceIdiom.Phone || DeviceInfo.Idiom == DeviceIdiom.Tablet;
        builder.Services.AddSingleton<ISettingsStore>(sp =>
            new SettingsStore(configPath, isMobile,
                logger: sp.GetRequiredService<ILogger<SettingsStore>>()));

        builder.Services.AddScoped<IClipboardService, MauiClipboardService>();
        builder.Services.AddSingleton<IJsonFieldExtractor, JsonFieldExtractor>();
        builder.Services.AddSingleton<IChartFieldRegistry, ChartFieldRegistry>();
        builder.Services.AddScoped<IChartDataService, ChartDataService>();
        builder.Services.AddScoped<IThemes, Themes>();

        builder.Services.Configure<PluginConfig>(builder.Configuration.GetSection("Plugins"));
        builder.Services.PostConfigure<PluginConfig>(cfg =>
        {
#if WINDOWS || MACCATALYST
            var userPlugins = Path.Combine(FileSystem.Current.AppDataDirectory, "plugins");
            Directory.CreateDirectory(userPlugins);
            var appPlugins = Path.Combine(AppContext.BaseDirectory, "Plugins");
            PluginFolderDefaults.Apply(cfg, FileSystem.Current.AppDataDirectory, userPlugins, appPlugins);
#else
            if (cfg.PluginFolders.Count > 0)
                PluginFolderDefaults.Apply(cfg, AppContext.BaseDirectory);
#endif
        });
        builder.Services.AddSingleton<PluginRegistry>(sp =>
        {
            var config = sp.GetRequiredService<IOptions<PluginConfig>>().Value;
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            return MqttProbePluginStartup.BuildPluginRegistry(config, loggerFactory);
        });
        builder.Services.AddSingleton<PayloadPipeline>();

        builder.Services.AddSingleton<ISparkplugTopologyService>(sp =>
            new SparkplugTopologyService(
                sp.GetRequiredService<IManagedMqttClient>(),
                sp.GetRequiredService<ILogger<SparkplugTopologyService>>(),
                autoSubscribeToClient: false));

        return builder.Build();
    }
}
