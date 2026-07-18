using MqttProbe.Services.Plugins;

namespace MqttProbe.Models.Plugins;

public sealed class PluginConfig
{
    public List<string> PluginFolders { get; init; } = [];

    public HashSet<string> DisabledPluginIds { get; init; } = [];

    public List<PluginOverrideConfig> Overrides { get; init; } = [];
}
