using System.Runtime.InteropServices;

namespace MqttProbe.Core.Models.Mqtt;

[StructLayout(LayoutKind.Auto)]
public readonly record struct SelectedTopicToken(Guid StoreId, long Generation, long ContentVersion);

// Key is the selected row's identity: a real topic path, or a grouping id that no topic can
// collide with. Label is what a caller shows instead of the key when they differ.
public sealed record SelectedTopicState(
    SelectedTopicToken Token,
    string? Key,
    int MessageCount,
    string? Label = null);
