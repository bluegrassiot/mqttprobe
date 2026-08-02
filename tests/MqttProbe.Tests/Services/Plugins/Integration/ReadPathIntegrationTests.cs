using System.Text;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MqttProbe.Services.Plugins.BuiltIn;
using MqttProbe.Services.Plugins.Contracts;
using MqttProbe.Services.Plugins.Pipeline;
using MqttProbe.Services.Plugins.Registry;
using Org.Eclipse.Tahu.Protobuf;

namespace MqttProbe.Tests.Services.Plugins.Integration;

[TestFixture]
public class ReadPathIntegrationTests
{
    private static PluginRegistry _registry = null!;
    private static PayloadPipeline _pipeline = null!;

    [OneTimeSetUp]
    public void SetUp()
    {
        var builder = new PluginRegistryBuilder();
        BuiltInPluginRegistration.RegisterBuiltIns(builder);
        _registry = builder.Build();
        _pipeline = new PayloadPipeline(_registry, Substitute.For<ILogger<PayloadPipeline>>());
    }

    private static MqttApplicationMessageReceivedEventArgs MakeArgs(string topic, byte[] payload)
    {
        var appMsg = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .Build();
        var packet = new MQTTnet.Packets.MqttPublishPacket { Topic = topic };
        return new MqttApplicationMessageReceivedEventArgs("test-client", appMsg, packet, null);
    }

    private static MqttApplicationMessageReceivedEventArgs MakeArgs(string topic, string payload)
        => MakeArgs(topic, Encoding.UTF8.GetBytes(payload));

    private static MqttApplicationMessageReceivedEventArgs MakeArgsNoPayload(string topic)
    {
        var appMsg = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .Build();
        var packet = new MQTTnet.Packets.MqttPublishPacket { Topic = topic };
        return new MqttApplicationMessageReceivedEventArgs("test-client", appMsg, packet, null);
    }

    // --- Empty payload ---

    [Test]
    public void EmptyPayload_ReturnsEmptyEnvelope_NoTopologyEvents()
    {
        var result = _pipeline.ProcessInbound(MakeArgsNoPayload("sensor/temp"));

        result.Envelope.IsFailure.Should().BeFalse();
        result.Envelope.FormatId.Should().Be("empty");
        result.Envelope.DisplayText.Should().BeEmpty();
        result.TopologyEvents.Should().BeEmpty();
    }

    [Test]
    public void EmptyStringPayload_ReturnsEmptyEnvelope_NoTopologyEvents()
    {
        var result = _pipeline.ProcessInbound(MakeArgs("sensor/temp", ""));

        result.Envelope.IsFailure.Should().BeFalse();
        result.Envelope.FormatId.Should().Be("empty");
        result.Envelope.DisplayText.Should().BeEmpty();
        result.TopologyEvents.Should().BeEmpty();
    }

    // --- Sparkplug B: valid NBIRTH ---

    [Test]
    public void Sparkplug_ValidNBirth_ReturnsSuccessEnvelope_WithNodeBirthEvent()
    {
        var payload = new Payload
        {
            Timestamp = 1234567890,
            Seq = 1
        };
        payload.Metrics.Add(new Payload.Types.Metric
        {
            Name = "Temperature",
            Datatype = 3,
            IntValue = 42
        });
        var bytes = payload.ToByteArray();

        var result = _pipeline.ProcessInbound(
            MakeArgs("spBv1.0/mygroup/NBIRTH/eon1", bytes));

        result.Envelope.IsFailure.Should().BeFalse();
        result.Envelope.FormatId.Should().Be("sparkplug-b");
        result.Envelope.TypedPayload.Should().NotBeNull();
        result.Envelope.TypedPayload.Should().BeOfType<Payload>();

        result.TopologyEvents.Should().HaveCount(1);
        var birthEvent = result.TopologyEvents[0].Should().BeOfType<NodeBirthEvent>().Subject;
        birthEvent.GroupId.Should().Be("mygroup");
        birthEvent.NodeId.Should().Be("eon1");
        birthEvent.Metrics.Should().HaveCount(1);
        birthEvent.Metrics[0].Name.Should().Be("Temperature");
    }

    // --- Sparkplug B: invalid protobuf ---

    [Test]
    public void Sparkplug_InvalidProtobuf_ReturnsFailureEnvelope_NoTopologyEvents()
    {
        var result = _pipeline.ProcessInbound(
            MakeArgs("spBv1.0/group/NDATA/eon1", "not protobuf"));

        result.Envelope.IsFailure.Should().BeTrue();
        result.Envelope.FormatId.Should().Be("sparkplug-b");
        result.Envelope.FailureReason.Should().Contain("parse failed");
        result.TopologyEvents.Should().BeEmpty();
    }

    // --- JSON ---

    [Test]
    public void Json_ValidPayload_ReturnsSuccessEnvelope_NoTopologyEvents()
    {
        const string json = """{"temp":21.5}""";
        var result = _pipeline.ProcessInbound(MakeArgs("sensor/data", json));

        result.Envelope.IsFailure.Should().BeFalse();
        result.Envelope.FormatId.Should().Be("json");
        result.Envelope.DisplayText.Should().Be(json);
        result.TopologyEvents.Should().BeEmpty();
    }

    // --- XML ---

    [Test]
    public void Xml_ValidPayload_ReturnsSuccessEnvelope()
    {
        const string xml = "<root><temp>21.5</temp></root>";
        var result = _pipeline.ProcessInbound(MakeArgs("sensor/data", xml));

        result.Envelope.IsFailure.Should().BeFalse();
        result.Envelope.FormatId.Should().Be("xml");
        result.Envelope.DisplayText.Should().Be(xml);
        result.TopologyEvents.Should().BeEmpty();
    }

    // --- Hex ---

    [Test]
    public void Hex_ValidPayload_ReturnsSuccessEnvelope()
    {
        const string hex = "deadbeef";
        var result = _pipeline.ProcessInbound(MakeArgs("sensor/data", hex));

        result.Envelope.IsFailure.Should().BeFalse();
        result.Envelope.FormatId.Should().Be("hex");
        result.Envelope.DisplayText.Should().Be(hex);
        result.TopologyEvents.Should().BeEmpty();
    }

    // --- Base64 ---

    [Test]
    public void Base64_ValidPayload_ReturnsSuccessEnvelope()
    {
        const string b64 = "dGVzdA==";
        var result = _pipeline.ProcessInbound(MakeArgs("sensor/data", b64));

        result.Envelope.IsFailure.Should().BeFalse();
        result.Envelope.FormatId.Should().Be("base64");
        // dGVzdA== decodes to "test"
        result.Envelope.DisplayText.Should().Be("test");
        result.TopologyEvents.Should().BeEmpty();
    }

    // --- PlainText ---

    [Test]
    public void PlainText_ValidPayload_ReturnsSuccessEnvelope()
    {
        var result = _pipeline.ProcessInbound(MakeArgs("sensor/temp", "42.5"));

        result.Envelope.IsFailure.Should().BeFalse();
        result.Envelope.FormatId.Should().Be("plaintext");
        result.Envelope.DisplayText.Should().Be("42.5");
        result.TopologyEvents.Should().BeEmpty();
    }

    // --- MessagePack ---

    [Test]
    public void MessagePack_ValidPayload_ReturnsSuccessEnvelope()
    {
        // fixmap(1) => {"temperature": 22.5}
        byte[] msgPack =
        [
            0x81,
            0xAB, 0x74, 0x65, 0x6D, 0x70, 0x65, 0x72, 0x61, 0x74, 0x75, 0x72, 0x65,
            0xCB, 0x40, 0x36, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00
        ];

        var result = _pipeline.ProcessInbound(MakeArgs("sensor/msgpack", msgPack));

        result.Envelope.IsFailure.Should().BeFalse();
        result.Envelope.FormatId.Should().Be("messagepack");
        result.Envelope.DisplayText.Should().Be("""{"temperature":22.5}""");
        result.TopologyEvents.Should().BeEmpty();
    }

    // --- Binary (non-UTF8, non-sparkplug topic) ---

    [Test]
    public void Binary_NonUtf8Payload_ReturnsSuccessEnvelope_HexDump()
    {
        byte[] binary = [0x00, 0xFF, 0x01, 0x80, 0xFE];
        var result = _pipeline.ProcessInbound(MakeArgs("sensor/raw", binary));

        result.Envelope.IsFailure.Should().BeFalse();
        result.Envelope.FormatId.Should().Be("binary");
        result.Envelope.DisplayText.Should().Be("00ff0180fe");
        result.TopologyEvents.Should().BeEmpty();
    }
}
