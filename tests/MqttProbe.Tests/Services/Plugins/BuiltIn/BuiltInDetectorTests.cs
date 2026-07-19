using System.Text;
using MQTTnet;
using MqttProbe.Services.Plugins.BuiltIn;
using MqttProbe.Services.Plugins.Contracts;
using MqttProbe.Services.Plugins.Registry;

namespace MqttProbe.Tests.Services.Plugins.BuiltIn;

[TestFixture]
public class BuiltInDetectorTests
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

    private static PluginRegistry BuildRegistry()
    {
        var builder = new PluginRegistryBuilder();
        BuiltInPluginRegistration.RegisterBuiltIns(builder);
        return builder.Build();
    }

    // --- Individual detector FormatId/Priority ---

    [Test]
    public void Detectors_HaveCorrectFormatIds()
    {
        new EmptyPayloadDetector().FormatId.Should().Be("empty");
        new SparkplugPayloadDetector().FormatId.Should().Be("sparkplug-b");
        new MessagePackPayloadDetector().FormatId.Should().Be("messagepack");
        new BinaryPayloadDetector().FormatId.Should().Be("binary");
        new JsonPayloadDetector().FormatId.Should().Be("json");
        new XmlPayloadDetector().FormatId.Should().Be("xml");
        new HexPayloadDetector().FormatId.Should().Be("hex");
        new Base64PayloadDetector().FormatId.Should().Be("base64");
        new PlainTextPayloadDetector().FormatId.Should().Be("plaintext");
    }

    [Test]
    public void Detectors_HaveCorrectPriorities()
    {
        new EmptyPayloadDetector().Priority.Should().Be(1000);
        new SparkplugPayloadDetector().Priority.Should().Be(900);
        new MessagePackPayloadDetector().Priority.Should().Be(800);
        new BinaryPayloadDetector().Priority.Should().Be(700);
        new JsonPayloadDetector().Priority.Should().Be(600);
        new XmlPayloadDetector().Priority.Should().Be(500);
        new HexPayloadDetector().Priority.Should().Be(400);
        new Base64PayloadDetector().Priority.Should().Be(300);
        new PlainTextPayloadDetector().Priority.Should().Be(200);
    }

    // --- Registry ordering ---

    [Test]
    public void Registry_DetectorsAreOrderedByPriorityDescending()
    {
        var registry = BuildRegistry();
        registry.Detectors.Should().HaveCount(9);
        registry.Detectors[0].FormatId.Should().Be("empty");
        registry.Detectors[1].FormatId.Should().Be("sparkplug-b");
        registry.Detectors[2].FormatId.Should().Be("messagepack");
        registry.Detectors[3].FormatId.Should().Be("binary");
        registry.Detectors[4].FormatId.Should().Be("json");
        registry.Detectors[5].FormatId.Should().Be("xml");
        registry.Detectors[6].FormatId.Should().Be("hex");
        registry.Detectors[7].FormatId.Should().Be("base64");
        registry.Detectors[8].FormatId.Should().Be("plaintext");
    }

    // --- Empty ---

    [Test]
    public void Empty_NoPayload_ReturnsEmpty()
    {
        var registry = BuildRegistry();
        var detector = registry.FindDetector(MakeArgsNoPayload("sensor/temp"));
        detector.Should().NotBeNull();
        detector!.FormatId.Should().Be("empty");
    }

    // --- Sparkplug ---

    [Test]
    public void Sparkplug_TopicWithBinaryPayload_MatchesSparkplug()
    {
        var registry = BuildRegistry();
        var bytes = new byte[] { 0xFF, 0xFE };
        var detector = registry.FindDetector(MakeArgs("spBv1.0/group/NDATA/eon1", bytes));
        detector.Should().NotBeNull();
        detector!.FormatId.Should().Be("sparkplug-b");
    }

    [Test]
    public void Sparkplug_ExactPrefixWithBinaryPayload_MatchesSparkplug()
    {
        var registry = BuildRegistry();
        var bytes = new byte[] { 0xFF, 0xFE };
        var detector = registry.FindDetector(MakeArgs("spBv1.0", bytes));
        detector.Should().NotBeNull();
        detector!.FormatId.Should().Be("sparkplug-b");
    }

    [Test]
    public void Sparkplug_TopicWithValidUtf8Payload_MatchesSparkplug()
    {
        var registry = BuildRegistry();
        var detector = registry.FindDetector(MakeArgs("spBv1.0/group/NDATA/eon1", "42.5"));
        detector.Should().NotBeNull();
        detector!.FormatId.Should().Be("sparkplug-b");
    }

    [Test]
    public void Sparkplug_EmptyPayload_ReturnsEmpty()
    {
        var registry = BuildRegistry();
        var detector = registry.FindDetector(MakeArgsNoPayload("spBv1.0/group/NBIRTH/eon1"));
        detector.Should().NotBeNull();
        detector!.FormatId.Should().Be("empty");
    }

    [Test]
    public void Sparkplug_TopicMatchIsCaseSensitive()
    {
        var registry = BuildRegistry();
        var bytes = new byte[] { 0xFF, 0xFE };
        var detector = registry.FindDetector(MakeArgs("spBV1.0/group/NDATA/eon1", bytes));
        detector.Should().NotBeNull();
        detector!.FormatId.Should().Be("binary");
    }

    [Test]
    public void Sparkplug_TakesPrecedenceOverMessagePack()
    {
        var registry = BuildRegistry();
        var bytes = new byte[] { 0x80 };
        var detector = registry.FindDetector(MakeArgs("spBv1.0/group/NDATA/eon1", bytes));
        detector.Should().NotBeNull();
        detector!.FormatId.Should().Be("sparkplug-b");
    }

    // --- Binary ---

    [Test]
    public void Binary_NonSparkplugTopic_WithBinaryPayload_ReturnsBinary()
    {
        var registry = BuildRegistry();
        var bytes = new byte[] { 0xFF, 0xFE };
        var detector = registry.FindDetector(MakeArgs("sensor/data", bytes));
        detector.Should().NotBeNull();
        detector!.FormatId.Should().Be("binary");
    }

    // --- MessagePack ---

    [Test]
    public void MessagePack_FixmapPayload_ReturnsMessagePack()
    {
        var registry = BuildRegistry();
        var bytes = new byte[] { 0x80 };
        var detector = registry.FindDetector(MakeArgs("sensor/data", bytes));
        detector.Should().NotBeNull();
        detector!.FormatId.Should().Be("messagepack");
    }

    [Test]
    public void MessagePack_FixarrayPayload_ReturnsMessagePack()
    {
        var registry = BuildRegistry();
        var bytes = new byte[] { 0x90 };
        var detector = registry.FindDetector(MakeArgs("sensor/data", bytes));
        detector.Should().NotBeNull();
        detector!.FormatId.Should().Be("messagepack");
    }

    // --- JSON ---

    [Test]
    public void Json_ObjectPayload_ReturnsJson()
    {
        var registry = BuildRegistry();
        var detector = registry.FindDetector(MakeArgs("sensor/data", """{"temp":21.5}"""));
        detector.Should().NotBeNull();
        detector!.FormatId.Should().Be("json");
    }

    [Test]
    public void Json_ArrayPayload_ReturnsJson()
    {
        var registry = BuildRegistry();
        var detector = registry.FindDetector(MakeArgs("sensor/data", "[1,2,3]"));
        detector.Should().NotBeNull();
        detector!.FormatId.Should().Be("json");
    }

    // --- XML ---

    [Test]
    public void Xml_Payload_ReturnsXml()
    {
        var registry = BuildRegistry();
        var detector = registry.FindDetector(MakeArgs("sensor/data", "<root>value</root>"));
        detector.Should().NotBeNull();
        detector!.FormatId.Should().Be("xml");
    }

    // --- Hex ---

    [Test]
    public void Hex_LowercasePayload_ReturnsHex()
    {
        var registry = BuildRegistry();
        var detector = registry.FindDetector(MakeArgs("sensor/data", "deadbeef"));
        detector.Should().NotBeNull();
        detector!.FormatId.Should().Be("hex");
    }

    [Test]
    public void Hex_UpperCasePayload_ReturnsHex()
    {
        var registry = BuildRegistry();
        var detector = registry.FindDetector(MakeArgs("sensor/data", "DEADBEEF"));
        detector.Should().NotBeNull();
        detector!.FormatId.Should().Be("hex");
    }

    // --- Base64 ---

    [Test]
    public void Base64_ValidPayload_ReturnsBase64()
    {
        var registry = BuildRegistry();
        var detector = registry.FindDetector(MakeArgs("sensor/data", "dGVzdA=="));
        detector.Should().NotBeNull();
        detector!.FormatId.Should().Be("base64");
    }

    // --- PlainText ---

    [Test]
    public void PlainText_SimpleNumber_ReturnsPlainText()
    {
        var registry = BuildRegistry();
        var detector = registry.FindDetector(MakeArgs("sensor/data", "42.5"));
        detector.Should().NotBeNull();
        detector!.FormatId.Should().Be("plaintext");
    }

    // --- No match ---

    [Test]
    public void FindDetector_EmptyRegistry_ReturnsNull()
    {
        var builder = new PluginRegistryBuilder();
        var registry = builder.Build();
        var detector = registry.FindDetector(MakeArgs("sensor/data", "hello"));
        detector.Should().BeNull();
    }
}
