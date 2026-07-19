using Microsoft.Extensions.Logging;
using MQTTnet;
using MqttProbe.Services.Plugins.Contracts;
using MqttProbe.Services.Plugins.Registry;

namespace MqttProbe.Services.Plugins.Pipeline;

public sealed class PayloadPipeline
{
    private readonly PluginRegistry _registry;
    private readonly ILogger<PayloadPipeline> _logger;

    public PayloadPipeline(PluginRegistry registry, ILogger<PayloadPipeline> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    public PipelineDecodeResult ProcessInbound(MqttApplicationMessageReceivedEventArgs e)
    {
        var diagnostics = new List<string>();
        var topic = e.ApplicationMessage.Topic;
        var segment = e.ApplicationMessage.GetPayloadSegment();
        var rawPayload = segment.Array is null ? [] : segment.ToArray();

        var detector = _registry.FindDetector(e);

        if (detector is null)
        {
            diagnostics.Add("No detector matched for incoming message.");
            return PipelineDecodeResult.Failure("unknown", topic, rawPayload, diagnostics);
        }

        var formatId = detector.FormatId;
        var decoder = _registry.FindDecoder(formatId);

        if (decoder is null)
        {
            diagnostics.Add($"No decoder found for format '{formatId}'.");
            return PipelineDecodeResult.Failure(formatId, topic, rawPayload, diagnostics);
        }

        DecodedPayloadEnvelope envelope;

        try
        {
            envelope = decoder.Decode(e);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Decoder for format '{FormatId}' threw an exception.", formatId);
            diagnostics.Add($"Decoder threw: {ex.Message}");
            return PipelineDecodeResult.Failure(formatId, topic, rawPayload, diagnostics);
        }

        if (envelope.IsFailure)
        {
            return new PipelineDecodeResult
            {
                Envelope = envelope,
                TopologyEvents = [],
                Diagnostics = diagnostics.AsReadOnly()
            };
        }

        var extractor = _registry.FindTopologyExtractor(formatId);

        if (extractor is null)
        {
            return new PipelineDecodeResult
            {
                Envelope = envelope,
                TopologyEvents = [],
                Diagnostics = diagnostics.AsReadOnly()
            };
        }

        IReadOnlyList<TopologyEvent> topologyEvents;

        try
        {
            topologyEvents = extractor.Extract(envelope);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Topology extractor for format '{FormatId}' threw an exception.", formatId);
            diagnostics.Add($"Topology extractor threw: {ex.Message}");
            topologyEvents = [];
        }

        return new PipelineDecodeResult
        {
            Envelope = envelope,
            TopologyEvents = topologyEvents,
            Diagnostics = diagnostics.AsReadOnly()
        };
    }

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
