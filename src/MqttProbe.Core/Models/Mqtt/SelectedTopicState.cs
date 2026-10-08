using System.Runtime.InteropServices;

namespace MqttProbe.Core.Models.Mqtt;

[StructLayout(LayoutKind.Auto)]
public readonly record struct SelectedTopicToken(Guid StoreId, long Generation, long ContentVersion);

public sealed record SelectedTopicState(SelectedTopicToken Token, string? FullTopic, int MessageCount);
