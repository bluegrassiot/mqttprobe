using System.Text;
using MQTTnet;
using MQTTnet.Client;
using MqttProbe.Services.Chart;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Mqtt;
using MqttProbe.Services.Platform;
using MqttProbe.Services.Security;
using MqttProbe.Services.Sparkplug;
using MqttProbe.Services.Telemetry;

namespace MqttProbe.Shared.Tests.Services.Sparkplug;

[TestFixture]
public class PayloadDecoderTests
{
    private static MqttApplicationMessageReceivedEventArgs MakeArgs(string topic, string payload)
    {
        var appMsg = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .Build();
        var packet = new MQTTnet.Packets.MqttPublishPacket { Topic = topic };
        return new MqttApplicationMessageReceivedEventArgs("test-client", appMsg, packet, null);
    }

    private static MqttApplicationMessageReceivedEventArgs MakeArgsNoPayload(string topic)
    {
        var appMsg = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .Build();
        var packet = new MQTTnet.Packets.MqttPublishPacket { Topic = topic };
        return new MqttApplicationMessageReceivedEventArgs("test-client", appMsg, packet, null);
    }

    private static MqttApplicationMessageReceivedEventArgs MakeArgsBytes(string topic, byte[] payload)
    {
        var appMsg = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .Build();
        var packet = new MQTTnet.Packets.MqttPublishPacket { Topic = topic };
        return new MqttApplicationMessageReceivedEventArgs("test-client", appMsg, packet, null);
    }

    [Test]
    public void GetPayloadStr_RegularTopic_TextPayload_ReturnsText()
    {
        PayloadDecoder.GetPayloadStr(MakeArgs("sensor/temp", "42.5")).Should().Be("42.5");
    }

    [Test]
    public void GetPayloadStr_RegularTopic_JsonPayload_ReturnsJson()
    {
        const string json = """{"temp":21.5}""";
        PayloadDecoder.GetPayloadStr(MakeArgs("sensor/data", json)).Should().Be(json);
    }

    [Test]
    public void GetPayloadStr_RegularTopic_EmptyStringPayload_ReturnsEmpty()
    {
        PayloadDecoder.GetPayloadStr(MakeArgs("sensor/temp", "")).Should().BeEmpty();
    }

    [Test]
    public void GetPayloadStr_RegularTopic_NoPayload_ReturnsEmpty()
    {
        PayloadDecoder.GetPayloadStr(MakeArgsNoPayload("sensor/temp")).Should().BeEmpty();
    }

    [Test]
    public void GetPayloadStr_SparkplugTopic_EmptyPayload_ReturnsEmpty()
    {
        PayloadDecoder.GetPayloadStr(MakeArgs("spBv1.0/group/NBIRTH/eon1", "")).Should().BeEmpty();
    }

    [Test]
    public void GetPayloadStr_SparkplugTopic_NoPayload_ReturnsEmpty()
    {
        PayloadDecoder.GetPayloadStr(MakeArgsNoPayload("spBv1.0/group/NBIRTH/eon1")).Should().BeEmpty();
    }

    [Test]
    public void GetPayloadStr_SparkplugTopic_PlainTextPayload_FallsBackToUtf8()
    {
        // Plain text is not valid protobuf — the exception is caught and decoding falls back to UTF-8.
        var result = PayloadDecoder.GetPayloadStr(MakeArgs("spBv1.0/group/DDATA/eon1", "not protobuf"));
        result.Should().Be("not protobuf");
    }

    [Test]
    public void GetPayloadStr_SparkplugTopic_InvalidProtobufBytes_DoesNotThrow()
    {
        var act = () => PayloadDecoder.GetPayloadStr(MakeArgs("spBv1.0/group/NDATA/eon1", "\x01\x02\x03invalid"));
        act.Should().NotThrow();
    }

    [Test]
    public void GetPayloadStr_SparkplugTopic_JsonPayload_FallsBackToUtf8()
    {
        const string json = """{"timestamp":123,"metrics":[]}""";
        var result = PayloadDecoder.GetPayloadStr(MakeArgs("spBv1.0/group/NDATA/eon1", json));
        result.Should().Be(json);
    }

    [Test]
    public void GetPayloadStr_TopicNotSpBv10_UsesUtf8Directly()
    {
        // "spBv2.0" does not match the "spBv1.0" prefix — should not attempt protobuf decoding.
        PayloadDecoder.GetPayloadStr(MakeArgs("spBv2.0/group/NBIRTH/eon1", "payload"))
            .Should().Be("payload");
    }

    [Test]
    public void GetPayloadStr_XmlPayload_ReturnsXmlString()
    {
        const string xml = "<root><temp>21.5</temp></root>";
        PayloadDecoder.GetPayloadStr(MakeArgs("sensor/data", xml)).Should().Be(xml);
    }

    [Test]
    public void GetPayloadStr_Base64Payload_ReturnsBase64String()
    {
        // Base64 strings are valid UTF-8 — the decoder returns them as-is.
        const string b64 = "SGVsbG8gV29ybGQ=";
        PayloadDecoder.GetPayloadStr(MakeArgs("sensor/data", b64)).Should().Be(b64);
    }

    [Test]
    public void GetPayloadStr_HexPayload_ReturnsHexString()
    {
        // Hex strings are valid UTF-8 — the decoder returns them as-is.
        const string hex = "4a6f686e";
        PayloadDecoder.GetPayloadStr(MakeArgs("sensor/data", hex)).Should().Be(hex);
    }

    [Test]
    public void GetPayloadStr_BinaryNonUtf8Payload_ReturnsHexDump()
    {
        // Raw non-UTF-8 bytes should be returned as a lowercase hex string.
        byte[] binary = [0x00, 0xFF, 0x01, 0x80, 0xFE];
        PayloadDecoder.GetPayloadStr(MakeArgsBytes("sensor/raw", binary))
            .Should().Be("00ff0180fe");
    }

    [Test]
    public void GetPayloadStr_MessagePackPayload_ReturnsJson()
    {
        byte[] msgPack =
        [
            0x81,
            0xAB, 0x74, 0x65, 0x6D, 0x70, 0x65, 0x72, 0x61, 0x74, 0x75, 0x72, 0x65,
            0xCB, 0x40, 0x36, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00
        ];

        PayloadFormatDetector.Detect(MakeArgsBytes("sensor/msgpack", msgPack))
            .Should().Be(DetectedPayloadFormat.MsgPack);
        PayloadDecoder.GetPayloadStr(MakeArgsBytes("sensor/msgpack", msgPack))
            .Should().Be("""{"temperature":22.5}""");
    }

    [Test]
    public void GetPayloadStr_BinaryValidUtf8Payload_ReturnsUtf8String()
    {
        // Bytes that happen to be valid UTF-8 should be returned as a string.
        byte[] utf8 = Encoding.UTF8.GetBytes("hello");
        PayloadDecoder.GetPayloadStr(MakeArgsBytes("sensor/raw", utf8))
            .Should().Be("hello");
    }

    [Test]
    public void GetPayloadStr_JsonArrayPayload_ReturnsJsonArray()
    {
        const string json = "[1,2,3]";
        PayloadDecoder.GetPayloadStr(MakeArgs("sensor/data", json)).Should().Be(json);
    }
}
