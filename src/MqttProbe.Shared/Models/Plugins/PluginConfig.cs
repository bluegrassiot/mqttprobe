using MqttProbe.Services.Plugins;

namespace MqttProbe.Models.Plugins;

public sealed class PluginConfig
{
    public List<string> PluginFolders { get; init; } = [];

    private readonly HashSet<string> _disabledPluginIds = new(StringComparer.OrdinalIgnoreCase);

    // Matched case-insensitively. Hand-editing this list is the only way to disable a plugin,
    // and manifest IDs are lowercase by validator rule, so nothing legitimate distinguishes
    // 'demo' from 'Demo' -- an ordinal match only ever swallowed a typo. The init accessor
    // re-wraps rather than stores, so the comparer survives DisabledPluginIds = ["demo"] in an
    // object initializer; a plain auto-property would swap in an ordinal set and undo the fix.
    public HashSet<string> DisabledPluginIds
    {
        get => _disabledPluginIds;
        init => _disabledPluginIds = new HashSet<string>(value, StringComparer.OrdinalIgnoreCase);
    }

    public List<PluginOverrideConfig> Overrides { get; init; } = [];

    public bool AllowBinaryPackages { get; init; } = true;
}
