using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MqttProbe.Desktop.Interop;
using MqttProbe.Desktop.Services;
using MqttProbe.Desktop.Services.Security;
using MqttProbe.Models.Plugins;
using MqttProbe.Services;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Platform;
using MqttProbe.Services.Plugins;
using MqttProbe.Services.Plugins.Packaging;
using MqttProbe.Services.Security;
using Photino.Blazor;
using Velopack;

namespace MqttProbe.Desktop;

internal static class Program
{
    private const string WebViewUserDataFolderVariable = "WEBVIEW2_USER_DATA_FOLDER";

    [STAThread]
    private static void Main(string[] args)
    {
        VelopackApp.Build().Run();

        ConfigureWebViewUserDataFolder();

        var builder = PhotinoBlazorAppBuilder.CreateDefault(args);

        builder.Services.AddMqttProbeMud();

        builder.Services.AddLogging(logging =>
        {
            logging.AddConsole();
            logging.AddDebug();
        });

        builder.Services.AddMqttProbeCore(HostSessionModel.SingleSession);
        ConfigureServices(builder);

        builder.RootComponents.Add<Main>("app");

        var app = builder.Build();

        app.Services.GetRequiredService<IPhotinoWindowAccessor>().Window = app.MainWindow;

        InitializeStorage(app);
        ConfigureWindow(app);

        app.Run();
    }

    private static string GetConfigDir() => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "mqttprobe");

    // WebView2 needs a user data folder it can create and write to. Left to Photino's default it
    // fails to build its environment on some machines: no browser process is ever spawned, nothing
    // is served, and the window paints black with no error on any channel. Pin it to the per-user
    // config directory the app already owns. Windows-only; WebView2 is not used on other platforms.
    private static void ConfigureWebViewUserDataFolder()
    {
        if (!OperatingSystem.IsWindows()
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(WebViewUserDataFolderVariable)))
        {
            return;
        }

        var dataDir = Path.Combine(GetConfigDir(), "webview2");
        Directory.CreateDirectory(dataDir);
        Environment.SetEnvironmentVariable(WebViewUserDataFolderVariable, dataDir);
    }

    private static void ConfigureServices(PhotinoBlazorAppBuilder builder)
    {
        builder.Services.AddSingleton<IAppInfoService, DesktopAppInfoService>();
        builder.Services.AddSingleton<IUpdateService, DesktopVelopackUpdateService>();
        builder.Services.AddAuthorizationCore();
        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddScoped<AuthenticationStateProvider, DesktopUnauthenticatedStateProvider>();

        var configDir = GetConfigDir();
        Directory.CreateDirectory(configDir);
        var secretsDir = Path.Combine(configDir, "secrets");
        Directory.CreateDirectory(secretsDir);

        AddSecretStorage(builder, secretsDir);

        builder.Services.AddSingleton<IPhotinoWindowAccessor, PhotinoWindowAccessor>();
        builder.Services.AddSingleton<ICertificateEnvelopeKeyStore>(sp =>
            new DesktopCertificateEnvelopeKeyStore(sp.GetRequiredService<ISecretStorage>()));
        builder.Services.AddSingleton<IFileProtector, DefaultFileProtector>();

        var configPath = Path.Combine(configDir, "appsettings.json");
        var configuration = new ConfigurationBuilder()
            .SetBasePath(configDir)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .Build();
        builder.Services.AddSingleton<IConfiguration>(configuration);

        builder.Services.AddMqttProbeSettings(configPath);

        builder.Services.AddSingleton<ICertificateAssetStore>(sp =>
        {
            var store = new CertificateAssetStore(
                sp.GetRequiredService<ICertificateEnvelopeKeyStore>(),
                configDir,
                sp.GetRequiredService<ILogger<CertificateAssetStore>>());
            return store;
        });
        builder.Services.AddSingleton<ICertificateFilePicker>(sp =>
            new DesktopCertificateFilePicker(sp.GetRequiredService<IPhotinoWindowAccessor>()));
        builder.Services.AddSingleton<ICertificateInputCapability, DesktopCertificateInputCapability>();

        builder.Services.AddScoped<IClipboardService, DesktopClipboardService>();
        builder.Services.AddMqttProbeCharts();

        AddPluginServices(builder, configuration, configDir);

        builder.Services.AddMqttProbeSparkplugTopology(HostSessionModel.SingleSession);
    }

    private static void AddSecretStorage(PhotinoBlazorAppBuilder builder, string secretsDir)
    {
        builder.Services.AddSingleton(_ =>
        {
            ISecretKeyProtector os;
            if (OperatingSystem.IsWindows())
                os = new WindowsDpapiSecretKeyProtector(secretsDir);
            else if (OperatingSystem.IsMacOS())
                os = new MacKeychainSecretKeyProtector(new MacKeychainNative());
            else if (OperatingSystem.IsLinux())
                os = new LinuxLibsecretKeyProtector(new LinuxLibsecretNative());
            else
                os = new UnavailableSecretKeyProtector();

            var raw = new RawSecretKeyFile(secretsDir);
            var file = new FileSecretKeyProtector(raw);
            return new DesktopSecretKeyProtector(secretsDir, os, file, raw);
        });
        builder.Services.AddSingleton<ISecretProtectionStatus>(sp =>
            sp.GetRequiredService<DesktopSecretKeyProtector>());
        builder.Services.AddSingleton<ISecretStorage>(sp =>
            new DesktopSecretStorage(secretsDir, sp.GetRequiredService<DesktopSecretKeyProtector>()));
    }

    private static void AddPluginServices(
        PhotinoBlazorAppBuilder builder, IConfiguration configuration, string configDir)
    {
        builder.Services.Configure<PluginConfig>(configuration.GetSection("Plugins"));
        builder.Services.PostConfigure<PluginConfig>(cfg =>
        {
            var userPlugins = Path.Combine(configDir, "plugins");
            Directory.CreateDirectory(userPlugins);
            var appPlugins = Path.Combine(AppContext.BaseDirectory, "Plugins");
            PluginFolderDefaults.Apply(cfg, configDir, userPlugins, appPlugins);
        });
        builder.Services.AddSingleton<IPluginPackagePicker, DesktopPluginPackagePicker>();
        builder.Services.AddSingleton<IPluginInputCapability, DesktopPluginInputCapability>();
        builder.Services.AddMqttProbePlugins();
    }

    private static void InitializeStorage(PhotinoBlazorApp app)
    {
        try
        {
            var keyProtector = app.Services.GetRequiredService<DesktopSecretKeyProtector>();
            keyProtector.InitializeAsync().GetAwaiter().GetResult();

            var configLoaded = app.Services.GetRequiredService<ISettingsLoader>()
                .LoadAsync().GetAwaiter().GetResult();

            var certCleanup = new CertificateStoreCleanup(
                app.Services.GetRequiredService<ICertificateAssetStore>(),
                app.Services.GetRequiredService<ICertificateEnvelopeKeyStore>(),
                app.Services.GetRequiredService<ILogger<CertificateStoreCleanup>>());
            certCleanup.RunAsync(
                    app.Services.GetRequiredService<IConnectionSettings>().Connections, configLoaded)
                .GetAwaiter().GetResult();
        }
        catch (SecretStorageException ex)
        {
            var message = "MQTTProbe could not initialize secret storage:\n\n" + ex.Message
                + "\n\nIf secrets are unrecoverable, remove the secrets directory under your user config (e.g. %USERPROFILE%\\.config\\mqttprobe\\secrets) and re-enter passwords.";
            Console.Error.WriteLine(message);
            if (OperatingSystem.IsWindows())
                ShowWindowsError(message);
            Environment.Exit(1);
        }
    }

    private static void ConfigureWindow(PhotinoBlazorApp app)
    {
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
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string lpText, string lpCaption, uint uType);

    [SuppressMessage("ReSharper", "InconsistentNaming")]
    private static void ShowWindowsError(string message)
    {
        const uint MB_OK = 0x00000000;
        const uint MB_ICONERROR = 0x00000010;
        _ = MessageBoxW(IntPtr.Zero, message, "MQTTProbe - Secret Storage Error", MB_OK | MB_ICONERROR);
    }
}
