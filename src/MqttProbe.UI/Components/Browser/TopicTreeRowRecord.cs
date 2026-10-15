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
    ImmutableArray<string> SelectionTopics = default)
{
    // Grouping rows only organise the view; namespace and group rows are real store nodes.
    public bool IsVirtualTopic => Kind is not (TopicTreeRowKind.Topic
        or TopicTreeRowKind.SparkplugNamespace
        or TopicTreeRowKind.SparkplugGroup);

    // A grouping row has no topic of its own, so it selects every real topic it stands for.
    // A row that names one topic is the degenerate case and keeps the same shape.
    public ImmutableArray<string> SelectedTopics => SelectionTopics.IsDefaultOrEmpty
        ? [FullPath]
        : SelectionTopics;

    // The name a caller shows for the selection. With several topics covered, naming them all
    // would be unreadable, so the first is named and the rest counted.
    public string SelectionLabel => SelectedTopics.Length == 1
        ? SelectedTopics[0]
        : $"{SelectedTopics[0]} and {SelectedTopics.Length - 1} more";

    public TopicSelection ToSelection() => new(FullPath, SelectionLabel, SelectedTopics);
}
