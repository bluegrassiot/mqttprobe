// System.Globalization is used only inside the #if IOS block below; it reads as unused
// on every other target framework.
using System.Globalization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MqttProbe.Maui.Services;
using MqttProbe.Models.Plugins;
using MqttProbe.Services;
using MqttProbe.Services.Platform;
using MqttProbe.Services.Plugins;
using MqttProbe.Services.Plugins.Packaging;
using MqttProbe.Services.Security;

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

        builder.Services.AddMqttProbeMud();
        builder.Services.AddMauiBlazorWebView();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        builder.Services.AddMqttProbeCore(HostSessionModel.SingleSession);
        AddPlatformServices(builder);
        AddCertificateServices(builder);
        AddConfiguration(builder);

        builder.Services.AddScoped<IClipboardService, MauiClipboardService>();
        builder.Services.AddMqttProbeCharts();

        AddPluginServices(builder);
        builder.Services.AddMqttProbeSparkplugTopology(HostSessionModel.SingleSession);

        return builder.Build();
    }

    private static void AddPlatformServices(MauiAppBuilder builder)
    {
        builder.Services.AddSingleton<IAppInfoService, AppInfoService>();
#if WINDOWS
        builder.Services.AddSingleton<IUpdateService, WinUI.VelopackUpdateService>();
#elif MACCATALYST
        builder.Services.AddSingleton<IUpdateService, MacVelopackUpdateService>();
#else
        builder.Services.AddSingleton<IUpdateService, NoOpUpdateService>();
#endif
        builder.Services.AddAuthorizationCore();
        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddScoped<AuthenticationStateProvider, UnauthenticatedStateProvider>();
        builder.Services.AddTransient<MainPage>();
        builder.Services.AddSingleton<ISecretStorage>(new MauiSecretStorage());
    }

    private static void AddCertificateServices(MauiAppBuilder builder)
    {
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
    }

    private static void AddConfiguration(MauiAppBuilder builder)
    {
        var configDir = Path.Combine(FileSystem.Current.AppDataDirectory, "config");
        var configPath = Path.Combine(configDir, "appsettings.json");

        Directory.CreateDirectory(configDir);
        builder.Configuration.AddJsonFile(configPath, optional: true, reloadOnChange: false);

        var isMobile = DeviceInfo.Idiom == DeviceIdiom.Phone || DeviceInfo.Idiom == DeviceIdiom.Tablet;
        builder.Services.AddMqttProbeSettings(configPath, isMobile);
    }

    private static void AddPluginServices(MauiAppBuilder builder)
    {
        builder.Services.Configure<PluginConfig>(builder.Configuration.GetSection("Plugins"));
        builder.Services.PostConfigure<PluginConfig>(cfg =>
        {
            var userPlugins = Path.Combine(FileSystem.Current.AppDataDirectory, "plugins");
            Directory.CreateDirectory(userPlugins);
            var appPlugins = Path.Combine(AppContext.BaseDirectory, "Plugins");
            PluginFolderDefaults.Apply(cfg, FileSystem.Current.AppDataDirectory, userPlugins, appPlugins);
        });
        builder.Services.AddSingleton<IPluginPackagePicker, MauiPluginPackagePicker>();
        builder.Services.AddSingleton<IPluginInputCapability, MauiPluginInputCapability>();
        builder.Services.AddMqttProbePlugins();
    }
}
