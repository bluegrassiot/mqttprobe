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
    MessageType
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
    ImmutableArray<string> SelectionTopics = default,
    SparkplugSelectionScope? Scope = null)
{
    public bool IsVirtualTopic => Kind is not (TopicTreeRowKind.Topic
        or TopicTreeRowKind.SparkplugNamespace
        or TopicTreeRowKind.SparkplugGroup);

    public ImmutableArray<string> SelectedTopics => SelectionTopics.IsDefaultOrEmpty
        ? [FullPath]
        : SelectionTopics;

    // A scoped row's coverage grows as the store does, so its name is fixed; an unscoped row
    // names its first topic and counts the rest.
    public string SelectionLabel => Scope is { } scope ? ScopedLabel(scope) : UnscopedLabel();

    public TopicSelection ToSelection() => new(FullPath, SelectionLabel, SelectedTopics, Scope);

    private string UnscopedLabel() => SelectedTopics.Length == 1
        ? SelectedTopics[0]
        : $"{SelectedTopics[0]} and {SelectedTopics.Length - 1} more";

    private static string ScopedLabel(SparkplugSelectionScope scope) => scope.Kind switch
    {
        SparkplugSelectionKind.Device => $"{scope.GroupPath}/{scope.NodeId}/{scope.DeviceId}",
        SparkplugSelectionKind.Devices => $"{scope.GroupPath}/{scope.NodeId}/Devices",
        _ => $"{scope.GroupPath}/{scope.NodeId}"
    };
}
