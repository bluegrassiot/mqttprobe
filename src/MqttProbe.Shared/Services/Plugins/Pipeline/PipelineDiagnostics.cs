using MqttProbe.Services.Plugins.Contracts;

namespace MqttProbe.Services.Plugins.Pipeline;

public sealed class PipelineDecodeResult
{
    public required DecodedPayloadEnvelope Envelope { get; init; }
    public IReadOnlyList<TopologyEvent> TopologyEvents { get; init; } = [];
    public IReadOnlyList<string> Diagnostics { get; init; } = [];

    public static PipelineDecodeResult Failure(
        string formatId,
        string topic,
        byte[] rawPayload,
        List<string> diagnostics) =>
        new()
        {
            Envelope = DecodedPayloadEnvelope.CreateFailure(
                formatId,
                topic,
                rawPayload,
                failureReason: diagnostics.Count > 0 ? diagnostics[0] : "Unknown error"),
            TopologyEvents = [],
            Diagnostics = diagnostics.AsReadOnly()
        };
}
