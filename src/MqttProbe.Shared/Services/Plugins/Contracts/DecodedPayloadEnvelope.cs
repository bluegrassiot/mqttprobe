namespace MqttProbe.Services.Plugins.Contracts;

public sealed class DecodedPayloadEnvelope
{
    public required string FormatId { get; init; }
    public required string Topic { get; init; }
    public required byte[] RawPayload { get; init; }
    public required string DisplayText { get; init; }
    public object? TypedPayload { get; init; }
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
    public IReadOnlyDictionary<ulong, string>? AliasNames { get; init; }
    public bool IsFailure { get; init; }
    public string? FailureReason { get; init; }

    public static DecodedPayloadEnvelope CreateSuccess(
        string formatId, string topic, byte[] rawPayload, string displayText,
        object? typedPayload = null, IReadOnlyDictionary<string, string>? metadata = null,
        IReadOnlyDictionary<ulong, string>? aliasNames = null) =>
        new()
        {
            FormatId = formatId,
            Topic = topic,
            RawPayload = rawPayload,
            DisplayText = displayText,
            TypedPayload = typedPayload,
            Metadata = metadata,
            AliasNames = aliasNames,
            IsFailure = false
        };

    public static DecodedPayloadEnvelope CreateFailure(
        string formatId, string topic, byte[] rawPayload, string failureReason) =>
        new()
        {
            FormatId = formatId,
            Topic = topic,
            RawPayload = rawPayload,
            DisplayText = $"Decode failed: {failureReason}",
            IsFailure = true,
            FailureReason = failureReason
        };
}
