using Microsoft.Extensions.Logging;
using MQTTnet;
using MqttProbe.Services.Plugins.Contracts;
using MqttProbe.Services.Plugins.Registry;

namespace MqttProbe.Services.Plugins.Pipeline;

public sealed class PayloadPipeline
{
    private volatile PluginRegistry _registry;
    private readonly ILogger<PayloadPipeline> _logger;

    public PayloadPipeline(PluginRegistry registry, ILogger<PayloadPipeline> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    public PluginRegistry Registry => _registry;

    public void SwapRegistry(PluginRegistry registry) => _registry = registry;

    public PipelineDecodeResult ProcessInbound(MqttApplicationMessageReceivedEventArgs e)
    {
        var registry = _registry;
        var diagnostics = new List<string>();
        var topic = e.ApplicationMessage.Topic;
        var segment = e.ApplicationMessage.GetPayloadSegment();
        var rawPayload = segment.Array is null ? [] : segment.ToArray();

        var candidates = registry.FindMatchingDetectors(e).ToList();

        if (candidates.Count == 0)
        {
            diagnostics.Add("No detector matched for incoming message.");
            return PipelineDecodeResult.Failure("unknown", topic, rawPayload, diagnostics);
        }

        DecodedPayloadEnvelope? firstFailure = null;

        // S3267 false positive: loop has early returns and state tracking, not reducible to LINQ.
#pragma warning disable S3267
        foreach (var detector in candidates)
#pragma warning restore S3267
        {
            var formatId = detector.FormatId;
            var decoder = registry.FindDecoder(formatId);

            if (decoder is null)
            {
                diagnostics.Add($"No decoder found for format '{formatId}'.");
                continue;
            }

            if (!TryDecode(decoder, e, formatId, diagnostics, out var envelope, out var throwMessage))
            {
                firstFailure ??= DecodedPayloadEnvelope.CreateFailure(formatId, topic, rawPayload, throwMessage!);
                continue;
            }

            if (envelope.IsFailure)
            {
                firstFailure ??= envelope;
                diagnostics.Add($"Decoder for '{formatId}' returned failure: {envelope.FailureReason}");
                continue;
            }

            var extractor = registry.FindTopologyExtractor(formatId);
            var topologyEvents = extractor is null
                ? []
                : ExtractTopologyEvents(extractor, envelope, formatId, diagnostics);

            return BuildDecodeResult(envelope, topologyEvents, diagnostics);
        }

        if (firstFailure is not null)
        {
            return BuildDecodeResult(firstFailure, [], diagnostics);
        }

        return PipelineDecodeResult.Failure(candidates[0].FormatId, topic, rawPayload, diagnostics);
    }

    private bool TryDecode(
        IPayloadDecoder decoder,
        MqttApplicationMessageReceivedEventArgs e,
        string formatId,
        List<string> diagnostics,
        out DecodedPayloadEnvelope envelope,
        out string? throwMessage)
    {
        try
        {
            envelope = decoder.Decode(e);
            throwMessage = null;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Decoder for format '{FormatId}' threw an exception.", formatId);
            diagnostics.Add($"Decoder threw: {ex.Message}");
            envelope = default!;
            throwMessage = ex.Message;
            return false;
        }
    }

    private IReadOnlyList<TopologyEvent> ExtractTopologyEvents(
        ITopologyExtractor extractor,
        DecodedPayloadEnvelope envelope,
        string formatId,
        List<string> diagnostics)
    {
        try
        {
            return extractor.Extract(envelope);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Topology extractor for format '{FormatId}' threw an exception.", formatId);
            diagnostics.Add($"Topology extractor threw: {ex.Message}");
            return [];
        }
    }

    private static PipelineDecodeResult BuildDecodeResult(
        DecodedPayloadEnvelope envelope,
        IReadOnlyList<TopologyEvent> topologyEvents,
        List<string> diagnostics) =>
        new()
        {
            Envelope = envelope,
            TopologyEvents = topologyEvents,
            Diagnostics = diagnostics.AsReadOnly()
        };

    public byte[] EncodeOutbound(PayloadEncoderRequest request)
    {
        var encoder = _registry.FindEncoder(request.FormatId);

        if (encoder is null)
        {
            throw new InvalidOperationException(
                $"No encoder registered for format '{request.FormatId}'.");
        }

        return encoder.Encode(request);
    }
}
