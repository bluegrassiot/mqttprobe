namespace MqttProbe.UI.Components.Browser;

public enum TopicTreeRowKind
{
    Topic,
    SparkplugNamespace,
    SparkplugGroup,
    SparkplugNode,
    NodeMessages,
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
    TopicTreeRowKind Kind = TopicTreeRowKind.Topic
)
{
    // Grouping rows exist only to organise the view. Namespace and group rows are real
    // store nodes with their own messages, so they keep real-topic behaviour.
    public bool IsVirtualTopic => Kind is not (TopicTreeRowKind.Topic
        or TopicTreeRowKind.SparkplugNamespace
        or TopicTreeRowKind.SparkplugGroup);
}
