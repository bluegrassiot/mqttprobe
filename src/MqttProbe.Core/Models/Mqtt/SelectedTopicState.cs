using System.Runtime.InteropServices;

namespace MqttProbe.Core.Models.Mqtt;

[StructLayout(LayoutKind.Auto)]
public readonly record struct SelectedTopicToken(Guid StoreId, long Generation, long ContentVersion)
{
    public bool IsSameSelection(SelectedTopicToken other) =>
        StoreId == other.StoreId && Generation == other.Generation;

    public bool IsContentAtLeastAsNewAs(SelectedTopicToken other) =>
        IsSameSelection(other) && ContentVersion >= other.ContentVersion;
}

public sealed record SelectedTopicState(
    SelectedTopicToken Token,
    string? Key,
    int MessageCount,
    string? Label = null);
