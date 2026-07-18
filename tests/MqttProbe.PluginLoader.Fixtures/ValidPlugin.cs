using MqttProbe.Services.Plugins.Contracts;

namespace MqttProbe.PluginLoader.Fixtures;

public sealed class ValidPlugin : IMqttProbePlugin
{
    public string PluginId => "fixture-valid";
    public string Name => "Valid Fixture Plugin";
    public string? Description => "A test fixture plugin.";

    public void RegisterServices(IPluginRegistrationContext context)
    {
    }
}
