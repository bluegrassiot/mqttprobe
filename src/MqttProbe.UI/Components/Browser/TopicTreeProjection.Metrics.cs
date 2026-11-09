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

        foreach (var root in SparkplugMetricHierarchy.Build(snapshots))
            ownerRow.Children.Add(MetricRow(root, scope, owner, chartTopic));
    }

    private static ProjectionNode MetricRow(
        SparkplugMetricNode node,
        SparkplugSelectionScope scope,
        TopicTreeOwner owner,
        string? chartTopic)
    {
        var metric = node.Metric;
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
            MetricCount = node.MetricCount
        };

        foreach (var child in node.Children)
            row.Children.Add(MetricRow(child, scope, owner, chartTopic));

        return row;
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

    private static bool ContainsOwner(ProjectionNode node, SparkplugSelectionScope scope)
    {
        if (node.Kind is TopicTreeRowKind.SparkplugNode or TopicTreeRowKind.SparkplugDevice
            && Equals(node.Scope, scope))
            return true;

        return node.Children.Any(child => ContainsOwner(child, scope));
    }
}
