using Microsoft.Extensions.Logging;
using MqttProbe.Models.Plugins;
using MqttProbe.Services.Plugins.BuiltIn;
using MqttProbe.Services.Plugins.Loading;
using MqttProbe.Services.Plugins.Registry;

namespace MqttProbe.Services.Plugins;

public static class MqttProbePluginStartup
{
    public static PluginRegistry BuildPluginRegistry(PluginConfig config, ILoggerFactory loggerFactory)
    {
        var registryBuilder = new PluginRegistryBuilder();
        BuiltInPluginRegistration.RegisterBuiltIns(registryBuilder);

        try
        {
            var loader = new PluginLoader(config, loggerFactory.CreateLogger<PluginLoader>());
            var loadResult = loader.LoadPlugins();
            foreach (var plugin in loadResult.Plugins)
                registryBuilder.RegisterPlugin(plugin.PluginId, ctx => plugin.RegisterServices(ctx));
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger(typeof(MqttProbePluginStartup).FullName!)
                .LogError(ex, "Plugin loading failed; falling back to built-ins only.");
        }

        return registryBuilder.Build(config.DisabledPluginIds, config.Overrides);
    }
}
