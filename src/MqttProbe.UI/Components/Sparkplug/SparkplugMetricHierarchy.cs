using MqttProbe.Core.Models.Sparkplug;
using MqttProbe.Core.Utilities;

namespace MqttProbe.UI.Components.Sparkplug;

public enum SparkplugMetricViewMode
{
    Flat,
    Hierarchy
}

public sealed class SparkplugMetricNode
{
    internal SparkplugMetricNode(
        string label,
        string fullPath,
        int depth,
        SpbMetricSnapshot? metric,
        IReadOnlyList<SparkplugMetricNode> children,
        int metricCount)
    {
        Label = label;
        FullPath = fullPath;
        Depth = depth;
        Metric = metric;
        Children = children;
        MetricCount = metricCount;
    }

    public string Label { get; }

    public string FullPath { get; }

    public int Depth { get; }

    public SpbMetricSnapshot? Metric { get; }

    public IReadOnlyList<SparkplugMetricNode> Children { get; }

    public int MetricCount { get; }

    public bool HasChildren => Children.Count > 0;

    public bool IsMetric => Metric is not null;
}

public static class SparkplugMetricHierarchy
{
    public static IReadOnlyList<SparkplugMetricNode> Build(IEnumerable<SpbMetricSnapshot> metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);

        var roots = new Dictionary<string, Builder>(StringComparer.Ordinal);
        foreach (var metric in metrics)
        {
            if (string.IsNullOrWhiteSpace(metric.Name))
                continue;

            var segments = SplitPathSegments(metric.Name);
            if (segments.Length == 0)
                continue;

            var level = roots;
            Builder? node = null;
            var path = string.Empty;

            for (var index = 0; index < segments.Length; index++)
            {
                var segment = segments[index];
                path = index == 0 ? segment : $"{path}/{segment}";
                if (!level.TryGetValue(segment, out node))
                {
                    node = new Builder(segment.Length == 0 ? "(empty)" : segment, path);
                    level[segment] = node;
                }

                level = node.Children;
            }

            node!.Metric = metric;
            node.FullPath = metric.Name;
        }

        return Freeze(roots.Values, 0);
    }

    private static string[] SplitPathSegments(string name) => name.Split('/');

    public static IReadOnlyList<SparkplugMetricNode> Flatten(
        IReadOnlyList<SparkplugMetricNode> roots,
        IReadOnlySet<string> expandedPaths)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(expandedPaths);

        var rows = new List<SparkplugMetricNode>();
        foreach (var root in roots)
            Append(root);
        return rows;

        void Append(SparkplugMetricNode node)
        {
            rows.Add(node);
            if (!node.HasChildren || !expandedPaths.Contains(node.FullPath))
                return;

            foreach (var child in node.Children)
                Append(child);
        }
    }

    public static IEnumerable<string> SingleChildPaths(IReadOnlyList<SparkplugMetricNode> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        return EnumerateSingleChildPaths(roots);
    }

    private static IEnumerable<string> EnumerateSingleChildPaths(IReadOnlyList<SparkplugMetricNode> roots)
    {
        foreach (var root in roots)
            foreach (var path in Descend(root))
                yield return path;

        static IEnumerable<string> Descend(SparkplugMetricNode node)
        {
            if (node.HasChildren)
            {
                if (node.Children.Count == 1)
                    yield return node.FullPath;

                foreach (var child in node.Children)
                    foreach (var path in Descend(child))
                        yield return path;
            }
        }
    }

    private static List<SparkplugMetricNode> Freeze(IEnumerable<Builder> nodes, int depth)
    {
        var result = new List<SparkplugMetricNode>();
        foreach (var node in nodes
                     .OrderBy(item => item.Label, NaturalStringComparer.Instance)
                     .ThenBy(item => item.FullPath, StringComparer.Ordinal))
        {
            var children = Freeze(node.Children.Values, depth + 1);
            var metricCount = (node.Metric is null ? 0 : 1) + children.Sum(child => child.MetricCount);
            result.Add(new SparkplugMetricNode(
                node.Label,
                node.FullPath,
                depth,
                node.Metric,
                children,
                metricCount));
        }

        return result;
    }

    private sealed class Builder(string label, string fullPath)
    {
        public string Label { get; } = label;

        public string FullPath { get; set; } = fullPath;

        public SpbMetricSnapshot? Metric { get; set; }

        public Dictionary<string, Builder> Children { get; } = new(StringComparer.Ordinal);
    }
}
