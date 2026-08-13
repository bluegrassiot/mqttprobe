using System.Diagnostics;
using System.Reflection;
using MqttProbe.Core.Services.Platform;

namespace MqttProbe.Web.Services;

public class AppInfoService : IAppInfoService
{
    private readonly Func<string?> _assemblyInformationalVersionProvider;
    private readonly Func<string?> _processProductVersionProvider;

    public AppInfoService()
        : this(GetAssemblyInformationalVersion, GetProcessProductVersion)
    {
    }

    public AppInfoService(
        Func<string?> assemblyInformationalVersionProvider,
        Func<string?> processProductVersionProvider)
    {
        _assemblyInformationalVersionProvider = assemblyInformationalVersionProvider;
        _processProductVersionProvider = processProductVersionProvider;
    }

    public bool RequiresAuthentication => true;
    public bool IsNative => false;

    public string GetVersion() =>
        AppVersionResolver.Resolve(
            _assemblyInformationalVersionProvider,
            _processProductVersionProvider);

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
