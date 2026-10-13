using Microsoft.Extensions.Logging;
using MqttProbe.Core.Models.Plugins;

namespace MqttProbe.Core.Services.Plugins.Loading;

// Plugin assemblies load exactly once per process. Re-running PluginLoader on every
// reload would create a fresh AssemblyLoadContext per DLL and leak one per reload,
// and assembly changes require a restart anyway.
public sealed class PluginAssemblyCache
{
    private readonly Lock _gate = new();
    private readonly bool _assemblyPluginsSupported;

    private PluginLoadResult? _result;

    // Android still loads its configured or bundled DLLs, so this is narrower than
    // PluginAssemblyInspector.AssemblyPluginsSupported, which also gates binary installs.
    private static bool AssemblyPluginLoadingAllowed => !OperatingSystem.IsIOS();

    public PluginAssemblyCache()
        : this(AssemblyPluginLoadingAllowed)
    {
    }

    // Test seam: the disabled path is unreachable on a desktop test host.
    internal PluginAssemblyCache(bool assemblyPluginsSupported)
    {
        _assemblyPluginsSupported = assemblyPluginsSupported;
    }

    public PluginLoadResult GetOrLoad(PluginConfig config, ILoggerFactory loggerFactory)
    {
        // Held for the whole first load, not just the null check: a second concurrent caller must
        // block and receive the same result rather than racing a duplicate PluginLoader.LoadPlugins().
        lock (_gate)
        {
            if (_result is not null)
            {
                return _result;
            }

            if (!_assemblyPluginsSupported)
            {
                return _result = new PluginLoadResult(
                    [],
                    [new PluginDiagnosticEntry
                    {
                        Source = "loader",
                        Severity = DiagnosticSeverity.Info,
                        Message = "Assembly plugins are not supported on this platform; assembly loading was skipped."
                    }],
                    []);
            }

            return _result = new PluginLoader(config, loggerFactory.CreateLogger<PluginLoader>())
                .LoadPlugins();
        }
    }
}
