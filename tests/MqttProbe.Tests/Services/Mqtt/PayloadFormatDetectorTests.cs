using System.Text;
using MQTTnet;
using MQTTnet.Client;
using MqttProbe.Services.Mqtt;

namespace MqttProbe.Shared.Tests.Services.Mqtt;

[TestFixture]
public class PayloadFormatDetectorTests
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

    [Test]
    public void Detect_NoPayload_ReturnsEmpty()
    {
        PayloadFormatDetector.Detect(MakeArgsNoPayload("sensor/temp"))
            .Should().Be(DetectedPayloadFormat.Empty);
    }

    // --- Sparkplug (new code) ---

    [Test]
    public void Detect_SparkplugTopic_WithBinaryPayload_ReturnsSparkplug()
    {
        // 0xFF and 0xFE are not valid UTF-8 start bytes, simulating a real protobuf payload
        var bytes = new byte[] { 0xFF, 0xFE };
        PayloadFormatDetector.Detect(MakeArgs("spBv1.0/group/NDATA/eon1", bytes))
            .Should().Be(DetectedPayloadFormat.Sparkplug);
    }

    [Test]
    public void Detect_SparkplugTopicExactPrefix_WithBinaryPayload_ReturnsSparkplug()
    {
        var bytes = new byte[] { 0xFF, 0xFE };
        PayloadFormatDetector.Detect(MakeArgs("spBv1.0", bytes))
            .Should().Be(DetectedPayloadFormat.Sparkplug);
    }

    [Test]
    public void Detect_SparkplugTopic_WithValidUtf8Payload_ReturnsSparkplug()
    {
        // The topic prefix alone is the authoritative Sparkplug B indicator;
        // valid UTF-8 payload bytes must still be treated as Sparkplug protobuf.
        PayloadFormatDetector.Detect(MakeArgs("spBv1.0/group/NDATA/eon1", "42.5"))
            .Should().Be(DetectedPayloadFormat.Sparkplug);
    }

    [Test]
    public void Detect_SparkplugTopic_EmptyPayload_ReturnsEmpty()
    {
        // Empty check must take precedence over the Sparkplug check
        PayloadFormatDetector.Detect(MakeArgsNoPayload("spBv1.0/group/NBIRTH/eon1"))
            .Should().Be(DetectedPayloadFormat.Empty);
    }

    [Test]
    public void Detect_SparkplugTopic_TopicMatchIsCaseSensitive()
    {
        // "spBV1.0" (uppercase V) must NOT be treated as a Sparkplug topic
        var bytes = new byte[] { 0xFF, 0xFE };
        PayloadFormatDetector.Detect(MakeArgs("spBV1.0/group/NDATA/eon1", bytes))
            .Should().Be(DetectedPayloadFormat.Binary);
    }

    [Test]
    public void Detect_SparkplugTopic_TakesPrecedenceOverMsgPack()
    {
        // 0x80 is a valid MessagePack empty fixmap start byte but is not valid UTF-8
        // (it is a continuation byte in an invalid position). The Sparkplug check fires
        // first and must win over MessagePack detection.
        var bytes = new byte[] { 0x80 };
        PayloadFormatDetector.Detect(MakeArgs("spBv1.0/group/NDATA/eon1", bytes))
            .Should().Be(DetectedPayloadFormat.Sparkplug);
    }

    // --- Binary ---

    [Test]
    public void Detect_NonSparkplugTopic_WithBinaryPayload_ReturnsBinary()
    {
        var bytes = new byte[] { 0xFF, 0xFE };
        PayloadFormatDetector.Detect(MakeArgs("sensor/data", bytes))
            .Should().Be(DetectedPayloadFormat.Binary);
    }

    // --- MessagePack ---

    [Test]
    public void Detect_MessagePackPayload_ReturnsMsgPack()
    {
        // 0x80 is an empty fixmap — the smallest valid structured MessagePack value
        var bytes = new byte[] { 0x80 };
        PayloadFormatDetector.Detect(MakeArgs("sensor/data", bytes))
            .Should().Be(DetectedPayloadFormat.MsgPack);
    }

    [Test]
    public void Detect_MessagePackFixarrayPayload_ReturnsMsgPack()
    {
        // 0x90 is an empty fixarray
        var bytes = new byte[] { 0x90 };
        PayloadFormatDetector.Detect(MakeArgs("sensor/data", bytes))
            .Should().Be(DetectedPayloadFormat.MsgPack);
    }

    // --- JSON ---

    [Test]
    public void Detect_JsonObjectPayload_ReturnsJson()
    {
        PayloadFormatDetector.Detect(MakeArgs("sensor/data", """{"temp":21.5}"""))
            .Should().Be(DetectedPayloadFormat.Json);
    }

    [Test]
    public void Detect_JsonArrayPayload_ReturnsJson()
    {
        PayloadFormatDetector.Detect(MakeArgs("sensor/data", "[1,2,3]"))
            .Should().Be(DetectedPayloadFormat.Json);
    }

    // --- XML ---

    [Test]
    public void Detect_XmlPayload_ReturnsXml()
    {
        PayloadFormatDetector.Detect(MakeArgs("sensor/data", "<root>value</root>"))
            .Should().Be(DetectedPayloadFormat.Xml);
    }

    // --- Hex ---

    [Test]
    public void Detect_HexPayload_ReturnsHex()
    {
        PayloadFormatDetector.Detect(MakeArgs("sensor/data", "deadbeef"))
            .Should().Be(DetectedPayloadFormat.Hex);
    }

    [Test]
    public void Detect_HexUpperCasePayload_ReturnsHex()
    {
        PayloadFormatDetector.Detect(MakeArgs("sensor/data", "DEADBEEF"))
            .Should().Be(DetectedPayloadFormat.Hex);
    }

    // --- Base64 ---

    [Test]
    public void Detect_Base64Payload_ReturnsBase64()
    {
        // "dGVzdA==" is base64 for "test"
        PayloadFormatDetector.Detect(MakeArgs("sensor/data", "dGVzdA=="))
            .Should().Be(DetectedPayloadFormat.Base64);
    }

    // --- PlainText ---

    [Test]
    public void Detect_PlainTextPayload_ReturnsPlainText()
    {
        PayloadFormatDetector.Detect(MakeArgs("sensor/data", "42.5"))
            .Should().Be(DetectedPayloadFormat.PlainText);
    }
}
