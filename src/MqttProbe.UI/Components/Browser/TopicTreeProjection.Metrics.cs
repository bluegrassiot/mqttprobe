using System.Collections.Immutable;
using System.Text;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Models.Sparkplug;
using MqttProbe.UI.Components.Sparkplug;

namespace MqttProbe.UI.Components.Browser;

// The metric hierarchy a grouped owner row adds, and the owner whose exact metric row a
// browsing selection refers to. A null owner keeps every row while no metric is selected.
internal sealed record MetricProjection(
    Func<SparkplugSelectionScope, IReadOnlyList<SpbMetricSnapshot>> Source,
    SparkplugSelectionScope? SelectedOwner = null);

internal static partial class TopicTreeProjection
{
    private const int MetricRowOrder = 5;

    private static void AppendMetricRows(
        ProjectionNode ownerRow, MetricProjection? metrics, string? chartTopic)
    {
        if (metrics is null || ownerRow.Scope is not { } scope)
            return;

        var snapshots = metrics.Source(scope);
        if (snapshots.Count == 0)
            return;

        var owner = scope.Kind == SparkplugSelectionKind.Device
            ? TopicTreeOwner.Device
            : TopicTreeOwner.Node;

        var roots = SparkplugMetricHierarchy.Build(snapshots);
        var children = roots
            .Select(root => MetricRow(root, scope, owner, chartTopic))
            .ToList();
        var targets = children.SelectMany(child => child.MetricTargets).ToImmutableArray();
        var groupRow = new ProjectionNode
        {
            Id = MetricGroupExpansionId(scope),
            Label = "Metrics",
            Order = MetricRowOrder,
            Kind = TopicTreeRowKind.SparkplugMetricsGroup,
            Scope = scope,
            Owner = owner,
            Target = null!,
            BranchLabel = scope.DeviceId is { } deviceId
                ? $"{scope.NodeId}/{deviceId}"
                : scope.NodeId,
            MetricCount = roots.Sum(root => root.MetricCount),
            MetricTargets = targets
        };

        groupRow.Children.AddRange(children);
        ownerRow.Children.Add(groupRow);
    }

    private static ProjectionNode MetricRow(
        SparkplugMetricNode node,
        SparkplugSelectionScope scope,
        TopicTreeOwner owner,
        string? chartTopic)
    {
        var metric = node.Metric;
        var children = node.Children
            .Select(child => MetricRow(child, scope, owner, chartTopic))
            .ToList();
        var targets = ImmutableArray.CreateBuilder<MqttSparkplugMetricTarget>();
        if (metric is { } ownMetric)
            targets.Add(new MqttSparkplugMetricTarget(scope, ownMetric.Name));

        foreach (var child in children)
            targets.AddRange(child.MetricTargets);

        var row = new ProjectionNode
        {
            Id = MetricExpansionId(scope, node.FullPath),
            Label = node.Label,
            MatchText = node.FullPath,
            Order = MetricRowOrder,
            Kind = TopicTreeRowKind.SparkplugMetric,
            Scope = scope,
            Owner = owner,
            Target = null!,
            BranchLabel = node.FullPath.Length == 0 ? "/" : node.FullPath,
            MetricName = metric?.Name,
            MetricValue = metric?.Value,
            MetricDataType = metric?.DataType,
            MetricChartTopic = metric is null ? null : chartTopic,
            MetricChartable = metric is { } value && SparkplugChartTargets.IsChartable(value),
            MetricNumeric = metric is { } numeric && SparkplugNodeDisplay.IsNumericType(numeric.DataType),
            MetricCount = node.MetricCount,
            MetricTargets = targets.ToImmutable()
        };

        row.Children.AddRange(children);
        return row;
    }

    // The distinct marker and full owner fields keep this id clear of topic paths and metric ids.
    private static string MetricGroupExpansionId(SparkplugSelectionScope scope)
    {
        var builder = new StringBuilder(VirtualIdPrefix).Append("metrics:");
        AppendMetricField(builder, scope.Kind.ToString());
        AppendMetricField(builder, scope.GroupPath);
        AppendMetricField(builder, scope.NodeId);
        AppendMetricField(builder, scope.DeviceId);
        return builder.ToString();
    }

    private static string MetricExpansionId(SparkplugSelectionScope scope, string fullPath)
    {
        var builder = new StringBuilder(VirtualIdPrefix).Append("metric:");
        AppendMetricField(builder, scope.Kind.ToString());
        AppendMetricField(builder, scope.GroupPath);
        AppendMetricField(builder, scope.NodeId);
        AppendMetricField(builder, scope.DeviceId);
        AppendMetricField(builder, fullPath);
        return builder.ToString();
    }

    // Length-prefixed so an empty prefix level or a slash inside a field cannot alias another path.
    private static void AppendMetricField(StringBuilder builder, string? value)
    {
        if (value is null)
        {
            builder.Append("-;");
            return;
        }

        builder.Append(value.Length).Append(':').Append(value).Append(';');
    }

    public static IReadOnlySet<SparkplugSelectionScope> RepresentedMetricOwners(
        ImmutableArray<TopicNodeSnapshot> roots,
        bool groupSparkplugTopics,
        IEnumerable<SparkplugSelectionScope> candidates)
    {
        var wanted = new HashSet<SparkplugSelectionScope>(candidates);
        var found = new HashSet<SparkplugSelectionScope>();
        if (!groupSparkplugTopics || roots.IsDefaultOrEmpty || wanted.Count == 0)
            return found;

        foreach (var root in BuildRoots(roots, groupSparkplugTopics: true))
            Collect(root);

        return found;

        void Collect(ProjectionNode node)
        {
            if (node.Kind is TopicTreeRowKind.SparkplugNode or TopicTreeRowKind.SparkplugDevice
                && node.Scope is { } scope && wanted.Contains(scope))
                found.Add(scope);

            foreach (var child in node.Children)
                Collect(child);
        }
    }

    private static bool ContainsOwner(ProjectionNode node, SparkplugSelectionScope scope)
    {
        if (node.Kind is TopicTreeRowKind.SparkplugNode or TopicTreeRowKind.SparkplugDevice
            && Equals(node.Scope, scope))
            return true;

        return node.Children.Any(child => ContainsOwner(child, scope));
    }
}
