using Microsoft.Extensions.Logging;
using MqttProbe.Models.Plugins;

namespace MqttProbe.Services.Plugins.Loading;

// Plugin assemblies load exactly once per process. Re-running PluginLoader on every
// reload would create a fresh AssemblyLoadContext per DLL and leak one per reload,
// and assembly changes require a restart anyway.
public sealed class PluginAssemblyCache
{
    private readonly Lock _gate = new();

    private PluginLoadResult? _result;

    public PluginLoadResult GetOrLoad(PluginConfig config, ILoggerFactory loggerFactory)
    {
        // Held for the whole first load, not just the null check: a second concurrent caller must
        // block and receive the same result rather than racing a duplicate PluginLoader.LoadPlugins().
        lock (_gate)
        {
            return _result ??= new PluginLoader(config, loggerFactory.CreateLogger<PluginLoader>())
                .LoadPlugins();
        }
    }
}
