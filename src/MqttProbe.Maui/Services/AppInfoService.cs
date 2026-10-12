using System.Diagnostics;
using System.Reflection;
using MqttProbe.Core.Services.Platform;
using MqttProbe.Core.Services.Plugins.Packaging;

namespace MqttProbe.Maui.Services;

public class AppInfoService : IAppInfoService
{
    private readonly Func<string?> _appVersionProvider;
    private readonly Func<string?> _processProductVersionProvider;
    private readonly Func<string?> _assemblyInformationalVersionProvider;

    public AppInfoService()
        : this(GetAppVersion, GetProcessProductVersion, GetAssemblyInformationalVersion)
    {
    }

    public AppInfoService(
        Func<string?> appVersionProvider,
        Func<string?> processProductVersionProvider,
        Func<string?> assemblyInformationalVersionProvider)
    {
        _appVersionProvider = appVersionProvider;
        _processProductVersionProvider = processProductVersionProvider;
        _assemblyInformationalVersionProvider = assemblyInformationalVersionProvider;
    }

    public bool RequiresAuthentication => false;
    public bool IsNative => true;
    public bool IsOidcMode => false;
    public string? ProviderDisplayName => null;

    // Read by the settings UI to hide plugin install affordances on hosts that cannot import
    // packages at all. Config, folder defaults and the loader guard refuse independently.
    public bool SupportsPluginInstallation => true;

    // App Store guideline 2.5.2 bars downloadable code, not data: Android and iOS keep
    // importing schema packages but never load a DLL, matching the install-time inspector.
    public bool SupportsAssemblyPlugins => PluginAssemblyInspector.AssemblyPluginsSupported;

    public string GetVersion() =>
        AppVersionResolver.Resolve(
            _appVersionProvider,
            _processProductVersionProvider,
            _assemblyInformationalVersionProvider);

    private static string GetAppVersion() => AppInfo.Current.VersionString;

    private static string? GetProcessProductVersion()
    {
        var mainModulePath = Process.GetCurrentProcess().MainModule?.FileName;
        return mainModulePath == null
            ? null
            : FileVersionInfo.GetVersionInfo(mainModulePath).ProductVersion;
    }

    private static string? GetAssemblyInformationalVersion() =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
}
