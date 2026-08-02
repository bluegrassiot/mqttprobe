using MqttProbe.Services.Plugins.Contracts;

namespace MqttProbe.PluginLoader.Fixtures;

public sealed class ThrowingPlugin : IMqttProbePlugin
{
    public ThrowingPlugin()
    {
        throw new InvalidOperationException("Construction deliberately failed.");
    }

    public string PluginId => "fixture-throwing";
    public string Name => "Throwing Fixture Plugin";
    public string? Description => "A plugin that throws during construction.";

    public void RegisterServices(IPluginRegistrationContext context)
    {
    }
}
