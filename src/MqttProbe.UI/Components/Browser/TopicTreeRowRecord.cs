using System.Collections.Immutable;
using MqttProbe.Core.Models.Mqtt;

namespace MqttProbe.UI.Components.Browser;

public enum TopicTreeRowKind
{
    Topic,
    SparkplugNamespace,
    SparkplugGroup,
    SparkplugNode,
    Devices,
    SparkplugDevice,
    MessageType,
    SparkplugMetric,
    SparkplugMetricsGroup,
    Spelling
}

// A node or device owns the message-type rows grouped beneath it and shares its colour with them.
public enum TopicTreeOwner
{
    Node,
    Device
}

public enum TopicTreeSelectionState
{
    Unchecked,
    Mixed,
    Checked
}

public sealed record TopicTreeRowModel(
    string FullPath,
    string DisplayName,
    int Depth,
    bool HasChildren,
    int TopicCount,
    int MessageCount,
    bool IsExpanded,
    bool IsValueBearer,
    TopicTreeRowKind Kind = TopicTreeRowKind.Topic,
    SparkplugSelectionScope? Scope = null,
    TopicTreeOwner? Owner = null,
    bool IsMessageTopic = false)
{
    public bool IsVirtualTopic => Kind is not (TopicTreeRowKind.Topic
        or TopicTreeRowKind.Spelling
        or TopicTreeRowKind.SparkplugNamespace
        or TopicTreeRowKind.SparkplugGroup);

    public string RowKey => FullPath;

    // Every displayed level is a branch target. Virtual rows carry a Sparkplug scope instead of
    // a raw path, so a virtual id never reaches topic matching.
    public TopicBranchTarget Branch { get; init; } = null!;

    public string BranchLabel { get; init; } = string.Empty;

    public string? MetricName { get; init; }

    public string? MetricValue { get; init; }

    public string? MetricDataType { get; init; }

    public string? MetricChartTopic { get; init; }

    public bool MetricChartable { get; init; }

    public bool MetricNumeric { get; init; }

    public int MetricCount { get; init; }

    public ImmutableArray<MqttSparkplugMetricTarget> MetricTargets { get; init; } =
        ImmutableArray<MqttSparkplugMetricTarget>.Empty;

    public bool IsMetricRow => Kind == TopicTreeRowKind.SparkplugMetric;

    public bool IsMetricGroup => Kind == TopicTreeRowKind.SparkplugMetricsGroup;

    public bool IsMetricFolder => IsMetricRow && MetricName is null;

    public bool IsEmptyLevel => DisplayName.Length == 0;

    public bool CanSelect => Branch is not null || (IsMetricRow && MetricName is not null);

    private string MetricOwnerLabel
    {
        get
        {
            if (Scope is not { } scope)
                return BranchLabel;

            return scope.DeviceId is { } device
                ? $"{scope.GroupPath}/{scope.NodeId}/{device}"
                : $"{scope.GroupPath}/{scope.NodeId}";
        }
    }

    public string MetricCheckboxLabel
    {
        get
        {
            if (IsMetricGroup)
                return $"Select all metrics for {MetricOwnerLabel}";

            if (IsMetricFolder)
                return $"Select all metrics under {MetricOwnerLabel} {BranchLabel}";

            if (MetricName is { } metric)
                return HasChildren
                    ? $"Select metric \"{metric}\" for {MetricOwnerLabel} and everything below it"
                    : $"Select metric \"{metric}\" for {MetricOwnerLabel}";

            return MetricOwnerLabel;
        }
    }

    public string BranchAriaLabel
    {
        get
        {
            if (IsMetricGroup)
                return HasChildren ? "Expand or collapse Metrics" : "Metrics";

            if (IsMetricFolder)
                return HasChildren ? $"Expand metric folder \"{BranchLabel}\"" : $"Metric folder \"{BranchLabel}\"";

            if (IsMetricRow)
            {
                var metric = MetricName!;
                return HasChildren
                    ? $"Select metric \"{metric}\" and everything below it"
                    : $"Select metric \"{metric}\"";
            }

            if (Branch is null)
                return string.Empty;

            if (Branch.Kind == TopicBranchKind.Sparkplug)
                return HasChildren ? $"Select {BranchLabel} and everything below it" : $"Select {BranchLabel}";

            if (!IsEmptyLevel)
                return HasChildren ? $"Select \"{FullPath}\" and everything below it" : $"Select topic \"{FullPath}\"";

            if (FullPath.Length == 0)
                return "Select the empty first level and everything below it";

            return HasChildren
                ? $"Select the empty level at \"{FullPath}\" and everything below it"
                : $"Select the empty level at \"{FullPath}\"";
        }
    }

    public string EmptyLevelTitle
    {
        get
        {
            if (FullPath.Length == 0)
                return "Empty first MQTT level · leading \"/\"";

            return IsMessageTopic
                ? $"Empty MQTT level · exact topic \"{FullPath}\""
                : $"Empty MQTT level · structure only · \"{FullPath}\"";
        }
    }

    // A real row opens its own topic path, so the selection keeps a path identity. A grouped
    // row has no topic of its own, so it opens the logical scope it stands for.
    public TopicSelection ToSelection()
    {
        if (!CanSelect)
            throw new InvalidOperationException("A metric container is expanded, not selected.");

        if (IsMetricRow && MetricName is { } metric)
            return TopicSelection.ForSparkplugMetric(Scope!, metric);

        return Branch.Kind == TopicBranchKind.Raw && Branch.Path is { } path
            ? TopicSelection.ForTopic(path)
            : TopicSelection.ForBranches([Branch]);
    }
}
