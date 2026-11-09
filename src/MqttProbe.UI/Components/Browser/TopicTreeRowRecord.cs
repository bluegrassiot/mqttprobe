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

    public bool IsMetricRow => Kind == TopicTreeRowKind.SparkplugMetric;

    public bool IsMetricFolder => IsMetricRow && MetricName is null;

    public bool IsEmptyLevel => DisplayName.Length == 0;

    public string BranchAriaLabel
    {
        get
        {
            if (IsMetricRow)
            {
                var metric = MetricName ?? BranchLabel;
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
        if (IsMetricRow)
            return TopicSelection.ForSparkplugMetric(Scope!, MetricName!);

        return Branch.Kind == TopicBranchKind.Raw && Branch.Path is { } path
            ? TopicSelection.ForTopic(path)
            : TopicSelection.ForBranches([Branch]);
    }
}
