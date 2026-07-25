using MqttProbe.Services.Plugins;

namespace MqttProbe.Models.Plugins;

public sealed class PluginConfig
{
    public List<string> PluginFolders { get; init; } = [];

    public HashSet<string> DisabledPluginIds { get; init; } = [];

    public List<PluginOverrideConfig> Overrides { get; init; } = [];

    // Uploading an assembly through the UI is remote code execution by design,
    // so it stays off unless an operator turns it on deliberately.
    public bool AllowBinaryPackages { get; init; }
}
