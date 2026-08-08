using System.Text;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MqttProbe.Services.Plugins.Contracts;
using MqttProbe.Services.Plugins.Pipeline;
using MqttProbe.Services.Plugins.Registry;
using NSubstitute.ExceptionExtensions;

namespace MqttProbe.Shared.Tests.Services.Plugins.Pipeline;

[TestFixture]
public class PayloadPipelineTests
{
    private static MqttApplicationMessageReceivedEventArgs MakeArgs(string topic, byte[] payload)
    {
        var appMsg = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .Build();
        var packet = new MQTTnet.Packets.MqttPublishPacket { Topic = topic };
        return new MqttApplicationMessageReceivedEventArgs("test-client", appMsg, packet, null);
    }

    private static IPayloadDetector MakeDetector(string formatId, bool canDetect)
    {
        var detector = Substitute.For<IPayloadDetector>();
        detector.FormatId.Returns(formatId);
        detector.Priority.Returns(100);
        detector.CanDetect(Arg.Any<MqttApplicationMessageReceivedEventArgs>()).Returns(canDetect);
        return detector;
    }

    private static IPayloadDecoder MakeDecoder(string formatId, DecodedPayloadEnvelope result)
    {
        var decoder = Substitute.For<IPayloadDecoder>();
        decoder.FormatId.Returns(formatId);
        decoder.Decode(Arg.Any<MqttApplicationMessageReceivedEventArgs>()).Returns(result);
        return decoder;
    }

    private static IPayloadDecoder MakeDecoder(string formatId, Exception throws)
    {
        var decoder = Substitute.For<IPayloadDecoder>();
        decoder.FormatId.Returns(formatId);
        decoder.Decode(Arg.Any<MqttApplicationMessageReceivedEventArgs>()).Throws(throws);
        return decoder;
    }

    private static ITopologyExtractor MakeTopologyExtractor(string formatId, IReadOnlyList<TopologyEvent> events)
    {
        var extractor = Substitute.For<ITopologyExtractor>();
        extractor.FormatId.Returns(formatId);
        extractor.Extract(Arg.Any<DecodedPayloadEnvelope>()).Returns(events);
        return extractor;
    }

    private static ITopologyExtractor MakeTopologyExtractor(string formatId, Exception throws)
    {
        var extractor = Substitute.For<ITopologyExtractor>();
        extractor.FormatId.Returns(formatId);
        extractor.Extract(Arg.Any<DecodedPayloadEnvelope>()).Throws(throws);
        return extractor;
    }

    private static IPayloadEncoder MakeEncoder(string formatId, byte[] result)
    {
        var encoder = Substitute.For<IPayloadEncoder>();
        encoder.FormatId.Returns(formatId);
        encoder.Encode(Arg.Any<PayloadEncoderRequest>()).Returns(result);
        return encoder;
    }

    private static PluginRegistry BuildRegistry(
        IPayloadDetector? detector = null,
        IPayloadDecoder? decoder = null,
        ITopologyExtractor? extractor = null,
        IPayloadEncoder? encoder = null)
    {
        var builder = new PluginRegistryBuilder();

        if (detector is not null)
        {
            builder.RegisterDetector(detector);
        }

        if (decoder is not null)
        {
            builder.RegisterDecoder(decoder);
        }

        if (extractor is not null)
        {
            builder.RegisterTopologyExtractor(extractor);
        }

        if (encoder is not null)
        {
            builder.RegisterEncoder(encoder);
        }

        return builder.Build();
    }

    private static IPayloadDetector MakeDetector(string formatId, bool canDetect, int priority)
    {
        var detector = Substitute.For<IPayloadDetector>();
        detector.FormatId.Returns(formatId);
        detector.Priority.Returns(priority);
        detector.CanDetect(Arg.Any<MqttApplicationMessageReceivedEventArgs>()).Returns(canDetect);
        return detector;
    }

    private static PluginRegistry BuildRegistry(
        IEnumerable<IPayloadDetector> detectors,
        IEnumerable<IPayloadDecoder> decoders,
        IEnumerable<ITopologyExtractor>? extractors = null)
    {
        var builder = new PluginRegistryBuilder();

        foreach (var d in detectors)
        {
            builder.RegisterDetector(d);
        }

        foreach (var d in decoders)
        {
            builder.RegisterDecoder(d);
        }

        if (extractors is not null)
        {
            foreach (var e in extractors)
            {
                builder.RegisterTopologyExtractor(e);
            }
        }

        return builder.Build();
    }

    private static PayloadPipeline MakePipeline(PluginRegistry registry)
    {
        var logger = Substitute.For<ILogger<PayloadPipeline>>();
        return new PayloadPipeline(registry, logger);
    }

    // --- ProcessInbound: valid message with topology events ---

    [Test]
    public void ProcessInbound_ValidMessage_ReturnsSuccessEnvelopeWithTopologyEvents()
    {
        var successEnvelope = DecodedPayloadEnvelope.CreateSuccess(
            "test-format", "t/p", [1, 2, 3], "decoded text");

        var topologyEvent = new NodeBirthEvent
        {
            FormatId = "test-format",
            Topic = "t/p",
            GroupId = "g1",
            NodeId = "n1",
            Metrics = []
        };

        var detector = MakeDetector("test-format", canDetect: true);
        var decoder = MakeDecoder("test-format", successEnvelope);
        var extractor = MakeTopologyExtractor("test-format", new List<TopologyEvent> { topologyEvent });

        var registry = BuildRegistry(detector, decoder, extractor);
        var pipeline = MakePipeline(registry);

        var result = pipeline.ProcessInbound(MakeArgs("t/p", [1, 2, 3]));

        result.Envelope.Should().BeSameAs(successEnvelope);
        result.TopologyEvents.Should().HaveCount(1);
        result.TopologyEvents[0].Should().BeSameAs(topologyEvent);
    }

    // --- ProcessInbound: no detector match ---

    [Test]
    public void ProcessInbound_NoDetector_ReturnsFailureEnvelopeWithDiagnostic()
    {
        var detector = MakeDetector("test-format", canDetect: false);
        var registry = BuildRegistry(detector);
        var pipeline = MakePipeline(registry);

        var result = pipeline.ProcessInbound(MakeArgs("sensor/temp", [0x01, 0x02]));

        result.Envelope.IsFailure.Should().BeTrue();
        result.Envelope.FormatId.Should().Be("unknown");
        result.Envelope.Topic.Should().Be("sensor/temp");
        result.Envelope.RawPayload.Should().Equal([0x01, 0x02]);
        result.TopologyEvents.Should().BeEmpty();
        result.Diagnostics.Should().Contain(d => d.Contains("detector"));
    }

    // --- ProcessInbound: detector matches but no decoder ---

    [Test]
    public void ProcessInbound_NoDecoder_ReturnsFailureEnvelopeWithDiagnostic()
    {
        var detector = MakeDetector("missing-format", canDetect: true);
        var registry = BuildRegistry(detector);
        var pipeline = MakePipeline(registry);

        var result = pipeline.ProcessInbound(MakeArgs("sensor/humidity", [0xFF, 0xFE]));

        result.Envelope.IsFailure.Should().BeTrue();
        result.Envelope.FormatId.Should().Be("missing-format");
        result.Envelope.Topic.Should().Be("sensor/humidity");
        result.Envelope.RawPayload.Should().Equal([0xFF, 0xFE]);
        result.TopologyEvents.Should().BeEmpty();
        result.Diagnostics.Should().Contain(d => d.Contains("decoder"));
    }

    // --- ProcessInbound: decoder throws ---

    [Test]
    public void ProcessInbound_DecoderThrows_ReturnsFailureEnvelopeWithDiagnostic()
    {
        var detector = MakeDetector("test-format", canDetect: true);
        var decoder = MakeDecoder("test-format", new InvalidOperationException("bad payload"));

        var registry = BuildRegistry(detector, decoder);
        var pipeline = MakePipeline(registry);

        var result = pipeline.ProcessInbound(MakeArgs("device/status", [0xDE, 0xAD]));

        result.Envelope.IsFailure.Should().BeTrue();
        result.Envelope.FormatId.Should().Be("test-format");
        result.Envelope.Topic.Should().Be("device/status");
        result.Envelope.RawPayload.Should().Equal([0xDE, 0xAD]);
        result.Envelope.FailureReason.Should().Be("bad payload");
        result.TopologyEvents.Should().BeEmpty();
        result.Diagnostics.Should().Contain(d => d.Contains("bad payload"));
    }

    // --- ProcessInbound: decoder returns IsFailure envelope ---

    [Test]
    public void ProcessInbound_DecoderReturnsFailure_ReturnsEnvelopeNoTopologyEvents()
    {
        var failureEnvelope = DecodedPayloadEnvelope.CreateFailure(
            "test-format", "t/p", [1], "decode error");

        var detector = MakeDetector("test-format", canDetect: true);
        var decoder = MakeDecoder("test-format", failureEnvelope);

        var registry = BuildRegistry(detector, decoder);
        var pipeline = MakePipeline(registry);

        var result = pipeline.ProcessInbound(MakeArgs("t/p", [1]));

        result.Envelope.Should().BeSameAs(failureEnvelope);
        result.TopologyEvents.Should().BeEmpty();
    }

    // --- ProcessInbound: topology extractor present and returns events ---

    [Test]
    public void ProcessInbound_ExtractorPresent_ReturnsEventsFromExtractor()
    {
        var successEnvelope = DecodedPayloadEnvelope.CreateSuccess(
            "test-format", "topic/1", [10], "display");

        var events = new List<TopologyEvent>
        {
            new NodeBirthEvent
            {
                FormatId = "test-format",
                Topic = "topic/1",
                GroupId = "g",
                NodeId = "n",
                Metrics = []
            },
            new NodeDataEvent
            {
                FormatId = "test-format",
                Topic = "topic/1",
                GroupId = "g",
                NodeId = "n",
                Metrics = []
            }
        };

        var detector = MakeDetector("test-format", canDetect: true);
        var decoder = MakeDecoder("test-format", successEnvelope);
        var extractor = MakeTopologyExtractor("test-format", events);

        var registry = BuildRegistry(detector, decoder, extractor);
        var pipeline = MakePipeline(registry);

        var result = pipeline.ProcessInbound(MakeArgs("topic/1", [10]));

        result.Envelope.Should().BeSameAs(successEnvelope);
        result.TopologyEvents.Should().HaveCount(2);
    }

    // --- ProcessInbound: topology extractor throws ---

    [Test]
    public void ProcessInbound_ExtractorThrows_ReturnsEnvelopeWithDiagnostic()
    {
        var successEnvelope = DecodedPayloadEnvelope.CreateSuccess(
            "test-format", "t/p", [1], "display");

        var detector = MakeDetector("test-format", canDetect: true);
        var decoder = MakeDecoder("test-format", successEnvelope);
        var extractor = MakeTopologyExtractor("test-format", new InvalidOperationException("extractor broke"));

        var registry = BuildRegistry(detector, decoder, extractor);
        var pipeline = MakePipeline(registry);

        var result = pipeline.ProcessInbound(MakeArgs("t/p", [1]));

        result.Envelope.Should().BeSameAs(successEnvelope);
        result.TopologyEvents.Should().BeEmpty();
        result.Diagnostics.Should().Contain(d => d.Contains("extractor broke"));
    }

    // --- Cascade: high fails envelope, lower succeeds ---

    [Test]
    public void ProcessInbound_HighFailsLowerSucceeds_ReturnsLowerFormatSuccess()
    {
        var failureEnvelope = DecodedPayloadEnvelope.CreateFailure(
            "high-format", "t/p", [1], "high failed");
        var successEnvelope = DecodedPayloadEnvelope.CreateSuccess(
            "low-format", "t/p", [1], "low decoded");

        var highDetector = MakeDetector("high-format", canDetect: true, priority: 200);
        var lowDetector = MakeDetector("low-format", canDetect: true, priority: 100);
        var highDecoder = MakeDecoder("high-format", failureEnvelope);
        var lowDecoder = MakeDecoder("low-format", successEnvelope);

        var registry = BuildRegistry(
            [highDetector, lowDetector],
            [highDecoder, lowDecoder]);
        var pipeline = MakePipeline(registry);

        var args = MakeArgs("t/p", [1]);
        var result = pipeline.ProcessInbound(args);

        result.Envelope.Should().BeSameAs(successEnvelope);
        result.Envelope.FormatId.Should().Be("low-format");
        result.TopologyEvents.Should().BeEmpty();

        lowDecoder.Received(1).Decode(args);
    }

    // --- Cascade: first succeeds, second never called ---

    [Test]
    public void ProcessInbound_FirstSucceeds_SecondDecoderNotCalled()
    {
        var successEnvelope = DecodedPayloadEnvelope.CreateSuccess(
            "first-format", "t/p", [1], "first decoded");

        var firstDetector = MakeDetector("first-format", canDetect: true, priority: 200);
        var secondDetector = MakeDetector("second-format", canDetect: true, priority: 100);
        var firstDecoder = MakeDecoder("first-format", successEnvelope);
        var secondDecoder = MakeDecoder("second-format",
            DecodedPayloadEnvelope.CreateSuccess("second-format", "t/p", [1], "second"));

        var registry = BuildRegistry(
            [firstDetector, secondDetector],
            [firstDecoder, secondDecoder]);
        var pipeline = MakePipeline(registry);

        var args = MakeArgs("t/p", [1]);
        var result = pipeline.ProcessInbound(args);

        result.Envelope.Should().BeSameAs(successEnvelope);
        secondDecoder.DidNotReceive().Decode(Arg.Any<MqttApplicationMessageReceivedEventArgs>());
    }

    // --- Cascade: all fail, first failure wins ---

    [Test]
    public void ProcessInbound_AllFail_ReturnsFirstFailureFormatAndEnvelope()
    {
        var firstFailure = DecodedPayloadEnvelope.CreateFailure(
            "first-format", "t/p", [1], "first error");
        var secondFailure = DecodedPayloadEnvelope.CreateFailure(
            "second-format", "t/p", [1], "second error");

        var firstDetector = MakeDetector("first-format", canDetect: true, priority: 200);
        var secondDetector = MakeDetector("second-format", canDetect: true, priority: 100);
        var firstDecoder = MakeDecoder("first-format", firstFailure);
        var secondDecoder = MakeDecoder("second-format", secondFailure);

        var registry = BuildRegistry(
            [firstDetector, secondDetector],
            [firstDecoder, secondDecoder]);
        var pipeline = MakePipeline(registry);

        var args = MakeArgs("t/p", [1]);
        var result = pipeline.ProcessInbound(args);

        result.Envelope.Should().BeSameAs(firstFailure);
        result.Envelope.FormatId.Should().Be("first-format");
        result.TopologyEvents.Should().BeEmpty();
        secondDecoder.Received(1).Decode(args);
    }

    // --- Cascade: high fails, lower succeeds with topology extractor ---

    [Test]
    public void ProcessInbound_HighFailsLowerSucceeds_UsesLowerTopologyExtractor()
    {
        var failureEnvelope = DecodedPayloadEnvelope.CreateFailure(
            "high-format", "t/p", [1], "high failed");
        var successEnvelope = DecodedPayloadEnvelope.CreateSuccess(
            "low-format", "t/p", [1], "low decoded");

        var topologyEvent = new NodeBirthEvent
        {
            FormatId = "low-format",
            Topic = "t/p",
            GroupId = "g1",
            NodeId = "n1",
            Metrics = []
        };

        var highDetector = MakeDetector("high-format", canDetect: true, priority: 200);
        var lowDetector = MakeDetector("low-format", canDetect: true, priority: 100);
        var highDecoder = MakeDecoder("high-format", failureEnvelope);
        var lowDecoder = MakeDecoder("low-format", successEnvelope);
        var highExtractor = MakeTopologyExtractor("high-format",
            [new NodeBirthEvent { FormatId = "high-format", Topic = "t/p", GroupId = "x", NodeId = "x", Metrics = [] }]);
        var lowExtractor = MakeTopologyExtractor("low-format", [topologyEvent]);

        var registry = BuildRegistry(
            [highDetector, lowDetector],
            [highDecoder, lowDecoder],
            [highExtractor, lowExtractor]);
        var pipeline = MakePipeline(registry);

        var result = pipeline.ProcessInbound(MakeArgs("t/p", [1]));

        result.Envelope.Should().BeSameAs(successEnvelope);
        result.Envelope.FormatId.Should().Be("low-format");
        result.TopologyEvents.Should().HaveCount(1);
        result.TopologyEvents[0].Should().BeSameAs(topologyEvent);
        highExtractor.DidNotReceive().Extract(Arg.Any<DecodedPayloadEnvelope>());
    }

    // --- Cascade: first match no decoder, second succeeds ---

    [Test]
    public void ProcessInbound_FirstNoDecoderSecondSucceeds_ReturnsSecondSuccess()
    {
        var successEnvelope = DecodedPayloadEnvelope.CreateSuccess(
            "second-format", "t/p", [1], "second decoded");

        var firstDetector = MakeDetector("first-format", canDetect: true, priority: 200);
        var secondDetector = MakeDetector("second-format", canDetect: true, priority: 100);
        var secondDecoder = MakeDecoder("second-format", successEnvelope);

        var registry = BuildRegistry(
            [firstDetector, secondDetector],
            [secondDecoder]);
        var pipeline = MakePipeline(registry);

        var result = pipeline.ProcessInbound(MakeArgs("t/p", [1]));

        result.Envelope.Should().BeSameAs(successEnvelope);
        result.TopologyEvents.Should().BeEmpty();
        result.Diagnostics.Should().Contain(d => d.Contains("first-format") && d.Contains("decoder"));
    }

    // --- Cascade: first decoder throws, second succeeds ---

    [Test]
    public void ProcessInbound_FirstThrowsSecondSucceeds_ReturnsSecondSuccess()
    {
        var successEnvelope = DecodedPayloadEnvelope.CreateSuccess(
            "second-format", "t/p", [1], "second decoded");

        var firstDetector = MakeDetector("first-format", canDetect: true, priority: 200);
        var secondDetector = MakeDetector("second-format", canDetect: true, priority: 100);
        var firstDecoder = MakeDecoder("first-format", new InvalidOperationException("first threw"));
        var secondDecoder = MakeDecoder("second-format", successEnvelope);

        var registry = BuildRegistry(
            [firstDetector, secondDetector],
            [firstDecoder, secondDecoder]);
        var pipeline = MakePipeline(registry);

        var result = pipeline.ProcessInbound(MakeArgs("t/p", [1]));

        result.Envelope.Should().BeSameAs(successEnvelope);
        result.TopologyEvents.Should().BeEmpty();
        result.Diagnostics.Should().Contain(d => d.Contains("first threw"));
    }

    // --- EncodeOutbound: finds encoder and returns bytes ---

    [Test]
    public void EncodeOutbound_EncoderFound_ReturnsBytes()
    {
        var expectedBytes = Encoding.UTF8.GetBytes("{\"value\":42}");
        var encoder = MakeEncoder("json", expectedBytes);

        var registry = BuildRegistry(encoder: encoder);
        var pipeline = MakePipeline(registry);

        var request = new PayloadEncoderRequest
        {
            Topic = "t/p",
            FormatId = "json",
            Metrics = new Dictionary<string, object> { ["value"] = 42 }
        };

        var result = pipeline.EncodeOutbound(request);

        result.Should().BeSameAs(expectedBytes);
    }

    // --- EncodeOutbound: missing FormatId throws ---

    [Test]
    public void EncodeOutbound_MissingFormatId_ThrowsInvalidOperationException()
    {
        var registry = BuildRegistry();
        var pipeline = MakePipeline(registry);

        var request = new PayloadEncoderRequest
        {
            Topic = "t/p",
            FormatId = "nonexistent",
            Metrics = new Dictionary<string, object>()
        };

        var act = () => pipeline.EncodeOutbound(request);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*nonexistent*");
    }
}
