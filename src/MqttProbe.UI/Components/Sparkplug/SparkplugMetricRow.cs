using MqttProbe.Core.Models.Sparkplug;

namespace MqttProbe.UI.Components.Sparkplug;

// MudTable keys rows by item, so passing fresh snapshot records would rebuild every
// row, and the chart button inside it, on each value update. Identity is the metric
// name; only Snapshot is replaced.
internal sealed class SparkplugMetricRow(string name, SpbMetricSnapshot snapshot)
{
    public string Name { get; } = name;

    public SpbMetricSnapshot Snapshot { get; set; } = snapshot;
}
