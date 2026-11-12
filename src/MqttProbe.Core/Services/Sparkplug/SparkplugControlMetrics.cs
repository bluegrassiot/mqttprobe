namespace MqttProbe.Core.Services.Sparkplug;

public static class SparkplugControlMetrics
{
    public const string NodeRebirth = "Node Control/Rebirth";
    public const string NodeReboot = "Node Control/Reboot";
    public const string NodeNextServer = "Node Control/Next Server";
    public const string NodeScanRate = "Node Control/Scan Rate";
    public const string DeviceRebirth = "Device Control/Rebirth";
    public const string DeviceReboot = "Device Control/Reboot";
    public const string DeviceScanRate = "Device Control/Scan Rate";

    // Case-insensitive on purpose: a near-miss spelling must not slip a reboot past its guard.
    private static readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase)
    {
        NodeRebirth,
        NodeReboot,
        NodeNextServer,
        NodeScanRate,
        DeviceRebirth,
        DeviceReboot,
        DeviceScanRate
    };

    public static bool IsControlMetric(string metricName) => _names.Contains(metricName.Trim());
}
