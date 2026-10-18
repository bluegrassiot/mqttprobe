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

// A node or device owns the message-type rows grouped beneath it and shares its colour with them.
public enum TopicTreeOwner
{
    Node,
    Device
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
    SparkplugSelectionScope? Scope = null,
    TopicTreeOwner? Owner = null)
{
    public bool IsVirtualTopic => Kind is not (TopicTreeRowKind.Topic
        or TopicTreeRowKind.SparkplugNamespace
        or TopicTreeRowKind.SparkplugGroup);

    public ImmutableArray<string> SelectedTopics => SelectionTopics.IsDefaultOrEmpty
        ? [FullPath]
        : SelectionTopics;

    public string SelectionLabel => Scope is { } scope ? ScopedLabel(scope) : UnscopedLabel();

    public TopicSelection ToSelection() => new(FullPath, SelectionLabel, SelectedTopics, Scope,
        RequireCanonicalTopics: Kind is TopicTreeRowKind.MessageType);

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
