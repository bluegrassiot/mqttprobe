using System.Diagnostics;
using System.Reflection;
using Microsoft.Maui.ApplicationModel;
using MqttProbe.Services.Platform;

namespace MqttProbe.Services;

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

    public string GetVersion() =>
        AppVersionResolver.Resolve(
            _appVersionProvider,
            _processProductVersionProvider,
            _assemblyInformationalVersionProvider);

    private static string? GetAppVersion() => AppInfo.Current.VersionString;

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
