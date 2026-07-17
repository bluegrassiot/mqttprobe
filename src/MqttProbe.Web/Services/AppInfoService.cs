using System.Diagnostics;
using System.Reflection;
using MqttProbe.Services.Platform;

namespace MqttProbe.Web.Services;

public class AppInfoService : IAppInfoService
{
    private readonly Func<string?> _processProductVersionProvider;
    private readonly Func<string?> _assemblyInformationalVersionProvider;

    public AppInfoService()
        : this(GetProcessProductVersion, GetAssemblyInformationalVersion)
    {
    }

    public AppInfoService(
        Func<string?> processProductVersionProvider,
        Func<string?> assemblyInformationalVersionProvider)
    {
        _processProductVersionProvider = processProductVersionProvider;
        _assemblyInformationalVersionProvider = assemblyInformationalVersionProvider;
    }

    public bool RequiresAuthentication => true;
    public bool IsNative => false;

    public string GetVersion() =>
        AppVersionResolver.Resolve(
            _processProductVersionProvider,
            _assemblyInformationalVersionProvider);

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
