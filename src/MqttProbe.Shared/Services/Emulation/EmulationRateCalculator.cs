using MqttProbe.Models.Emulation;

namespace MqttProbe.Services.Emulation;

public static class EmulationRateCalculator
{
    public const double HighThroughputThresholdMessagesPerSecond = 200;

    public static double ProjectedMessagesPerSecond(IEnumerable<EmulatorNodeConfig> nodes, int intervalMs)
    {
        if (intervalMs <= 0) return 0;
        return nodes.Sum(MessagesPerTick) * (1000.0 / intervalMs);
    }

    public static bool IsHighThroughput(double messagesPerSecond) =>
        messagesPerSecond >= HighThroughputThresholdMessagesPerSecond;

    private static int MessagesPerTick(EmulatorNodeConfig node)
    {
        // Birth/death messages are one-time and excluded from the steady-state projection.
        var devicesWithMetrics = node.Devices.Count(d => d.Metrics.Count > 0);
        return node.Type switch
        {
            EmulatorNodeType.SparkplugB => 1 + devicesWithMetrics,
            _ => node.PayloadFormat == GenericPayloadFormat.Json
                ? devicesWithMetrics
                : node.Devices.Sum(d => d.Metrics.Count)
        };
    }
}
