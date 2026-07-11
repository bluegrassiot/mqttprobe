using MqttProbe.Models.Mqtt;

namespace MqttProbe.Components.Browser;

public sealed record TopicTreeRowModel(
    MessageStore Store,
    string FullPath,
    string DisplayName,
    int Depth,
    bool HasChildren,
    int TopicCount,
    int MessageCount,
    bool IsExpanded,
    bool IsValueBearer
);
