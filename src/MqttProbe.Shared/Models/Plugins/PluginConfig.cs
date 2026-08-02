using MqttProbe.Services.Plugins;

namespace MqttProbe.Models.Plugins;

public sealed class PluginConfig
{
    public List<string> PluginFolders { get; init; } = [];

    private readonly HashSet<string> _disabledPluginIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Plugin IDs to skip, matched case-insensitively. Hand-editing this list is the only way
    /// to disable a plugin, and manifest IDs are lowercase by validator rule, so nothing
    /// legitimate distinguishes 'demo' from 'Demo' -- an ordinal match only ever swallowed a typo.
    /// </summary>
    /// <remarks>
    /// The init accessor re-wraps the assigned value rather than storing it, so the comparer
    /// survives <c>DisabledPluginIds = ["demo"]</c> in an object initializer. A plain
    /// auto-property would silently swap in an ordinal set there and undo the fix.
    /// </remarks>
    public HashSet<string> DisabledPluginIds
    {
        get => _disabledPluginIds;
        init => _disabledPluginIds = new HashSet<string>(value, StringComparer.OrdinalIgnoreCase);
    }

    public List<PluginOverrideConfig> Overrides { get; init; } = [];

    // On by default so the feature works on a host whose filesystem an operator cannot
    // reach. Installing an assembly runs third-party code in-process, so a deployment that
    // does not want that sets this false explicitly.
    public bool AllowBinaryPackages { get; init; } = true;
}
