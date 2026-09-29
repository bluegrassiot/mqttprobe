using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Options;
using MqttProbe.Core.Services.Platform;
using MqttProbe.Web.Authentication;

namespace MqttProbe.Web.Services;

public class AppInfoService : IAppInfoService
{
    private readonly Func<string?> _assemblyInformationalVersionProvider;
    private readonly Func<string?> _processProductVersionProvider;
    private readonly bool _isOidcMode;
    private readonly string? _providerDisplayName;

    public AppInfoService(IOptions<AuthenticationOptions> authOptions)
        : this(authOptions, GetAssemblyInformationalVersion, GetProcessProductVersion)
    {
    }

    internal AppInfoService(
        IOptions<AuthenticationOptions> authOptions,
        Func<string?> assemblyInformationalVersionProvider,
        Func<string?> processProductVersionProvider)
    {
        _assemblyInformationalVersionProvider = assemblyInformationalVersionProvider;
        _processProductVersionProvider = processProductVersionProvider;

        var options = authOptions.Value;

        _isOidcMode = string.Equals(options.Mode, "OIDC", StringComparison.OrdinalIgnoreCase);

        if (_isOidcMode)
            _providerDisplayName = options.Oidc.ProviderDisplayName;
    }

    public bool RequiresAuthentication => true;
    public bool IsNative => false;
    public bool IsOidcMode => _isOidcMode;
    public string? ProviderDisplayName => _isOidcMode ? _providerDisplayName : null;

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
