using System.Globalization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Extensions.ManagedClient;
using MqttProbe.Components.Layout;
using MqttProbe.Maui.Services;
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
        // WORKAROUND: MudBlazor satellite assemblies (MudBlazor.resources.dll) are not
        // bundled on iOS, causing a FileNotFoundException during localization. Force a
        // neutral culture so the runtime falls back to the embedded default resources.
        // See: https://github.com/MudBlazor/MudBlazor/issues — replace with proper
        // satellite assembly inclusion once a permanent fix is applied.
        CultureInfo.DefaultThreadCurrentCulture = new CultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("en-US");

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

        // Only iOS has a platform-specific secret store and file protection. Mac Catalyst runs
        // on macOS, which has no NSFileProtection, and the iOS types are themselves #if IOS.
#if IOS
        builder.Services.AddSingleton<ICertificateEnvelopeKeyStore, iOSCertificateEnvelopeKeyStore>();
        builder.Services.AddSingleton<IFileProtector>(new iOSFileProtector());
#else
        builder.Services.AddSingleton<ICertificateEnvelopeKeyStore>(sp =>
            new MauiCertificateEnvelopeKeyStore(sp.GetRequiredService<ISecretStorage>()));
        builder.Services.AddSingleton<IFileProtector, DefaultFileProtector>();
#endif

        // Separate question from the above: Apple's crypto stack cannot load a PKCS#12 with
        // X509KeyStorageFlags.Exportable -- both the ephemeral and the default key set throw
        // PlatformNotSupportedException -- so both Apple heads need the wrapper, which skips
        // the canonical re-export and stores the original bytes and password instead.
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
        // v1.0.1 and earlier shipped with the MAUI template's placeholder publisher
        // ("User Name"), so existing users' config lives under that directory.
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
        builder.Services.AddSingleton<ISparkplugTopologyService, SparkplugTopologyService>();
        builder.Services.AddSingleton<IPayloadDecoder, PayloadDecoder>();
        builder.Services.AddScoped<IThemes, Themes>();

        return builder.Build();
    }
}
