using MqttProbe.Services.Plugins;

namespace MqttProbe.Models.Plugins;

public sealed class PluginConfig
{
    public List<string> PluginFolders { get; init; } = [];

    public HashSet<string> DisabledPluginIds { get; init; } = [];

    public List<PluginOverrideConfig> Overrides { get; init; } = [];

    // On by default so the feature works on a host whose filesystem an operator cannot
    // reach. Installing an assembly runs third-party code in-process, so a deployment that
    // does not want that sets this false explicitly.
    public bool AllowBinaryPackages { get; init; } = true;
}
