using System.Diagnostics;
using System.Reflection;
using MqttProbe.Core.Services.Platform;

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

    // Read by the settings UI to hide import affordances on hosts that cannot import
    // packages at all. Config, folder defaults and the loader guard refuse independently.
    public bool SupportsPluginInstallation => true;

    // Tracks what the startup loader can actually run, so this is narrower than the
    // install-time inspector: Android still loads configured or bundled DLLs, and neither
    // mobile host accepts a binary archive.
    public bool SupportsAssemblyPlugins => !OperatingSystem.IsIOS();

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
