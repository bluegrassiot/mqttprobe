using System.Diagnostics;
using System.Reflection;
using MqttProbe.Services.Platform;

namespace MqttProbe.Services;

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

    public bool RequiresAuthentication => false;
    public bool IsNative => true;

    public string GetVersion()
    {
        var version = TryGetVersion(_processProductVersionProvider)
                      ?? TryGetVersion(_assemblyInformationalVersionProvider)
                      ?? "unknown";
        return TrimAfterPlus(version);
    }

    private static string? TryGetVersion(Func<string?> provider)
    {
        try
        {
            return provider();
        }
        catch
        {
            return null;
        }
    }

    private static string? GetProcessProductVersion()
    {
        var mainModulePath = Process.GetCurrentProcess().MainModule?.FileName;
        return mainModulePath == null
            ? null
            : FileVersionInfo.GetVersionInfo(mainModulePath).ProductVersion;
    }

    private static string? GetAssemblyInformationalVersion() =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    private static string TrimAfterPlus(string version)
    {
        var index = version.IndexOf('+');

        return index != -1 ? version[..index] : version;
    }
}
