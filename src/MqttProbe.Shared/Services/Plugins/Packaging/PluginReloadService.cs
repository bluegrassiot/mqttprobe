using Microsoft.Extensions.Logging;
using MqttProbe.Models.Plugins;
using MqttProbe.Services.Plugins.Loading;
using MqttProbe.Services.Plugins.Pipeline;

namespace MqttProbe.Services.Plugins.Packaging;

public sealed record PluginReloadOutcome(bool Succeeded, string? Error);

public sealed class PluginReloadService
{
    private readonly PluginConfig _config;
    private readonly PayloadPipeline _pipeline;
    private readonly PluginInstallSession _session;
    private readonly PluginAssemblyCache _assemblyCache;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<PluginReloadService> _logger;

    public PluginReloadService(
        PluginConfig config,
        PayloadPipeline pipeline,
        PluginInstallSession session,
        PluginAssemblyCache assemblyCache,
        ILoggerFactory loggerFactory)
    {
        _config = config;
        _pipeline = pipeline;
        _session = session;
        _assemblyCache = assemblyCache;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<PluginReloadService>();
    }

    public PluginReloadOutcome Reload()
    {
        try
        {
            var registry = MqttProbePluginStartup.BuildPluginRegistry(
                _config, _loggerFactory, _assemblyCache);

            _pipeline.SwapRegistry(registry);
            _session.ClearNonRestartEntries();

            _logger.LogInformation("Plugin registry reloaded with {Count} package(s).",
                registry.LoadedPackagePaths.Count);

            return new PluginReloadOutcome(true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Plugin reload failed; keeping the previous registry.");
            return new PluginReloadOutcome(false, ex.Message);
        }
    }
}
