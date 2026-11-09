using MqttProbe.Core.Models.Sparkplug;

namespace MqttProbe.UI.Components.Sparkplug;

internal sealed class SparkplugMetricRow(
    string key,
    string name,
    string fullPath,
    SpbMetricSnapshot? snapshot,
    int depth,
    bool hasChildren,
    bool isExpanded,
    int metricCount)
{
    public string Key { get; } = key;

    public string Name { get; set; } = name;

    public string FullPath { get; set; } = fullPath;

    public SpbMetricSnapshot? Snapshot { get; set; } = snapshot;

    public int Depth { get; set; } = depth;

    public bool HasChildren { get; set; } = hasChildren;

    public bool IsExpanded { get; set; } = isExpanded;

    public int MetricCount { get; set; } = metricCount;

    public bool IsMetric => Snapshot is not null;
}
