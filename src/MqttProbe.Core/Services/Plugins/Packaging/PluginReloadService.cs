using Microsoft.Extensions.Logging;
using MqttProbe.Core.Models.Plugins;
using MqttProbe.Core.Services.Plugins.Loading;
using MqttProbe.Core.Services.Plugins.Pipeline;
using MqttProbe.Core.Services.Sparkplug;

namespace MqttProbe.Core.Services.Plugins.Packaging;

public sealed record PluginReloadOutcome(bool Succeeded, string? Error);

public sealed class PluginReloadService
{
    private readonly PluginConfig _config;
    private readonly PayloadPipeline _pipeline;
    private readonly PluginInstallSession _session;
    private readonly PluginAssemblyCache _assemblyCache;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<PluginReloadService> _logger;
    private readonly SparkplugMetricMetadataStore? _metricMetadataStore;

    public PluginReloadService(
        PluginConfig config,
        PayloadPipeline pipeline,
        PluginInstallSession session,
        PluginAssemblyCache assemblyCache,
        ILoggerFactory loggerFactory,
        SparkplugMetricMetadataStore? metricMetadataStore = null)
    {
        _config = config;
        _pipeline = pipeline;
        _session = session;
        _assemblyCache = assemblyCache;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<PluginReloadService>();
        _metricMetadataStore = metricMetadataStore;
    }

    public PluginReloadOutcome Reload()
    {
        try
        {
            var registry = MqttProbePluginStartup.BuildPluginRegistry(
                _config, _loggerFactory, _assemblyCache, _metricMetadataStore);

            _pipeline.SwapRegistry(registry);
            _session.ClearNonRestartEntries();

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Plugin registry reloaded with {Count} package(s).",
                    registry.LoadedPackagePaths.Count);
            }

            return new PluginReloadOutcome(true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Plugin reload failed; keeping the previous registry.");
            return new PluginReloadOutcome(false, ex.Message);
        }
    }
}
