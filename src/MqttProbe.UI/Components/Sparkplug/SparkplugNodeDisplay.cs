using MqttProbe.Core.Models.Sparkplug;
using MqttProbe.Core.Utilities;

namespace MqttProbe.UI.Components.Sparkplug;

public static class SparkplugNodeDisplay
{
    public static bool IsStale(SpbNode node) =>
        node.Status != SpbNodeStatus.Online && node.LastBirthAt == null && node.LastDataAt != null;

    public static string NodeStatusColor(SpbNode node) =>
        IsStale(node) ? "var(--mud-palette-warning)" : StatusColor(node.Status);

    public static string NodeStatusLabel(SpbNode node) =>
        IsStale(node) ? "STALE" : StatusLabel(node.Status);

    public static string StatusColor(SpbNodeStatus status) => status switch
    {
        SpbNodeStatus.Online => "var(--mud-palette-success)",
        SpbNodeStatus.Offline => "var(--mud-palette-error)",
        _ => "var(--mud-palette-text-secondary)"
    };

    public static string StatusLabel(SpbNodeStatus status) => status switch
    {
        SpbNodeStatus.Online => "ONLINE",
        SpbNodeStatus.Offline => "OFFLINE",
        _ => "UNKNOWN"
    };

    public static bool IsNumericType(string dataType) =>
        dataType is "int8" or "int16" or "int32" or "int64"
            or "uint8" or "uint16" or "uint32" or "uint64"
            or "float" or "double";

    public static string ValueColor(SpbMetricSnapshot metric) =>
        IsNumericType(metric.DataType) ? "var(--mud-palette-success)" : "var(--mud-palette-text-secondary)";

    public static string NodeSecondaryText(SpbNode node)
    {
        var lastSeen = node.Status switch
        {
            SpbNodeStatus.Online => DisplayHelpers.GetRelativeTime(node.LastDataAt ?? node.LastBirthAt),
            SpbNodeStatus.Offline => DisplayHelpers.GetRelativeTime(node.LastDeathAt),
            _ => null
        };
        var parts = new List<string>();
        if (lastSeen != null) parts.Add(lastSeen);
        parts.Add($"{node.Metrics.Count} metric{(node.Metrics.Count != 1 ? "s" : "")}");
        parts.Add($"{node.Devices.Count} device{(node.Devices.Count != 1 ? "s" : "")}");
        return string.Join(" · ", parts);
    }

    public static string DeviceSecondaryText(SpbDevice device)
    {
        if (device.Status == SpbNodeStatus.Offline && device.LastDeathAt != null)
            return DisplayHelpers.GetRelativeTime(device.LastDeathAt);
        if (device.LastDataAt != null)
            return DisplayHelpers.GetRelativeTime(device.LastDataAt);
        return $"{device.Metrics.Count} metric{(device.Metrics.Count != 1 ? "s" : "")}";
    }
}
