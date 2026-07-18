using System.Text;
using Google.Protobuf;
using MQTTnet;
using MQTTnet.Client;
using MqttProbe.Services.Plugins.BuiltIn;
using MqttProbe.Services.Plugins.Contracts;
using MqttProbe.Services.Plugins.Registry;
using Org.Eclipse.Tahu.Protobuf;

namespace MqttProbe.Tests.Services.Plugins.BuiltIn;

[TestFixture]
public class BuiltInDecoderTests
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

    private static PluginRegistry BuildRegistry()
    {
        var builder = new PluginRegistryBuilder();
        BuiltInPluginRegistration.RegisterBuiltIns(builder);
        return builder.Build();
    }

    // --- Registry decoder registration ---

    [Test]
    public void Registry_AllNineDecodersRegistered()
    {
        var registry = BuildRegistry();
        registry.Decoders.Should().HaveCount(9);
        registry.FindDecoder("empty").Should().NotBeNull();
        registry.FindDecoder("sparkplug-b").Should().NotBeNull();
        registry.FindDecoder("messagepack").Should().NotBeNull();
        registry.FindDecoder("binary").Should().NotBeNull();
        registry.FindDecoder("json").Should().NotBeNull();
        registry.FindDecoder("xml").Should().NotBeNull();
        registry.FindDecoder("hex").Should().NotBeNull();
        registry.FindDecoder("base64").Should().NotBeNull();
        registry.FindDecoder("plaintext").Should().NotBeNull();
    }

    [Test]
    public void Registry_UnknownFormatId_ReturnsNull()
    {
        var registry = BuildRegistry();
        registry.FindDecoder("unknown-format").Should().BeNull();
    }

    // --- FormatId on decoder instances ---

    [Test]
    public void Decoders_HaveCorrectFormatIds()
    {
        new EmptyPayloadDecoder().FormatId.Should().Be("empty");
        new SparkplugPayloadDecoder().FormatId.Should().Be("sparkplug-b");
        new MessagePackPayloadDecoder().FormatId.Should().Be("messagepack");
        new BinaryPayloadDecoder().FormatId.Should().Be("binary");
        new JsonPayloadDecoder().FormatId.Should().Be("json");
        new XmlPayloadDecoder().FormatId.Should().Be("xml");
        new HexPayloadDecoder().FormatId.Should().Be("hex");
        new Base64PayloadDecoder().FormatId.Should().Be("base64");
        new PlainTextPayloadDecoder().FormatId.Should().Be("plaintext");
    }

    // --- Empty decoder ---

    [Test]
    public void EmptyDecoder_NoPayload_ReturnsEmptyDisplayText()
    {
        var decoder = new EmptyPayloadDecoder();
        var result = decoder.Decode(MakeArgsNoPayload("sensor/temp"));
        result.IsFailure.Should().BeFalse();
        result.DisplayText.Should().BeEmpty();
        result.FormatId.Should().Be("empty");
    }

    [Test]
    public void EmptyDecoder_EmptyStringPayload_ReturnsEmptyDisplayText()
    {
        var decoder = new EmptyPayloadDecoder();
        var result = decoder.Decode(MakeArgs("sensor/temp", ""));
        result.IsFailure.Should().BeFalse();
        result.DisplayText.Should().BeEmpty();
    }

    // --- JSON decoder ---

    [Test]
    public void JsonDecoder_ObjectPayload_ReturnsJsonString()
    {
        var decoder = new JsonPayloadDecoder();
        const string json = """{"temp":21.5}""";
        var result = decoder.Decode(MakeArgs("sensor/data", json));
        result.IsFailure.Should().BeFalse();
        result.DisplayText.Should().Be(json);
        result.FormatId.Should().Be("json");
    }

    [Test]
    public void JsonDecoder_ArrayPayload_ReturnsJsonString()
    {
        var decoder = new JsonPayloadDecoder();
        const string json = "[1,2,3]";
        var result = decoder.Decode(MakeArgs("sensor/data", json));
        result.IsFailure.Should().BeFalse();
        result.DisplayText.Should().Be(json);
    }

    // --- XML decoder ---

    [Test]
    public void XmlDecoder_Payload_ReturnsXmlString()
    {
        var decoder = new XmlPayloadDecoder();
        const string xml = "<root><temp>21.5</temp></root>";
        var result = decoder.Decode(MakeArgs("sensor/data", xml));
        result.IsFailure.Should().BeFalse();
        result.DisplayText.Should().Be(xml);
        result.FormatId.Should().Be("xml");
    }

    // --- PlainText decoder ---

    [Test]
    public void PlainTextDecoder_TextPayload_ReturnsText()
    {
        var decoder = new PlainTextPayloadDecoder();
        var result = decoder.Decode(MakeArgs("sensor/temp", "42.5"));
        result.IsFailure.Should().BeFalse();
        result.DisplayText.Should().Be("42.5");
        result.FormatId.Should().Be("plaintext");
    }

    // --- Hex decoder ---

    [Test]
    public void HexDecoder_HexPayload_ReturnsDecodedText()
    {
        var decoder = new HexPayloadDecoder();
        const string hex = "4a6f686e";
        var result = decoder.Decode(MakeArgs("sensor/data", hex));
        result.IsFailure.Should().BeFalse();
        result.DisplayText.Should().Be("John");
        result.FormatId.Should().Be("hex");
    }

    [Test]
    public void HexDecoder_HexPayload_JsonDecodedText()
    {
        var decoder = new HexPayloadDecoder();
        // {"temp":22.5} → 7B2274656D70223A32322E357D
        const string hex = "7b2274656d70223a32322e357d";
        var result = decoder.Decode(MakeArgs("sensor/data", hex));
        result.IsFailure.Should().BeFalse();
        result.DisplayText.Should().Be("""{"temp":22.5}""");
        result.FormatId.Should().Be("hex");
    }

    [Test]
    public void HexDecoder_InvalidHex_ReturnsFailure()
    {
        var decoder = new HexPayloadDecoder();
        var result = decoder.Decode(MakeArgs("sensor/data", "xyz!"));
        result.IsFailure.Should().BeTrue();
        result.FailureReason.Should().NotBeNullOrEmpty();
        result.DisplayText.Should().Contain("Decode failed");
    }

    // --- Base64 decoder ---

    [Test]
    public void Base64Decoder_Payload_ReturnsDecodedText()
    {
        var decoder = new Base64PayloadDecoder();
        const string b64 = "SGVsbG8gV29ybGQ=";
        var result = decoder.Decode(MakeArgs("sensor/data", b64));
        result.IsFailure.Should().BeFalse();
        result.DisplayText.Should().Be("Hello World");
        result.FormatId.Should().Be("base64");
    }

    [Test]
    public void Base64Decoder_JsonPayload_ReturnsDecodedJson()
    {
        var decoder = new Base64PayloadDecoder();
        const string b64 = "eyJ0ZW1wZXJhdHVyZSI6MjIuNSwicHJlc3N1cmUiOjEwMTMuMjUsImZsb3dSYXRlIjoxMi44LCJzdGF0dXMiOiJhY3RpdmUifQ==";
        var result = decoder.Decode(MakeArgs("sensor/data", b64));
        result.IsFailure.Should().BeFalse();
        result.DisplayText.Should().Be("""{"temperature":22.5,"pressure":1013.25,"flowRate":12.8,"status":"active"}""");
        result.FormatId.Should().Be("base64");
    }

    [Test]
    public void Base64Decoder_InvalidBase64_ReturnsFailure()
    {
        var decoder = new Base64PayloadDecoder();
        var result = decoder.Decode(MakeArgs("sensor/data", "not!valid@base64"));
        result.IsFailure.Should().BeTrue();
        result.FailureReason.Should().NotBeNullOrEmpty();
        result.DisplayText.Should().Contain("Decode failed");
    }

    // --- Binary decoder ---

    [Test]
    public void BinaryDecoder_NonUtf8Payload_ReturnsHexDump()
    {
        var decoder = new BinaryPayloadDecoder();
        byte[] binary = [0x00, 0xFF, 0x01, 0x80, 0xFE];
        var result = decoder.Decode(MakeArgsBytes("sensor/raw", binary));
        result.IsFailure.Should().BeFalse();
        result.DisplayText.Should().Be("00ff0180fe");
        result.FormatId.Should().Be("binary");
    }

    [Test]
    public void BinaryDecoder_EmptyPayload_ReturnsEmptyDisplayText()
    {
        var decoder = new BinaryPayloadDecoder();
        var result = decoder.Decode(MakeArgsBytes("sensor/raw", []));
        result.IsFailure.Should().BeFalse();
        result.DisplayText.Should().BeEmpty();
    }

    // --- MessagePack decoder ---

    [Test]
    public void MessagePackDecoder_ValidPayload_ReturnsJsonString()
    {
        var decoder = new MessagePackPayloadDecoder();
        // fixmap(1) => {"temperature": 22.5}
        byte[] msgPack =
        [
            0x81,
            0xAB, 0x74, 0x65, 0x6D, 0x70, 0x65, 0x72, 0x61, 0x74, 0x75, 0x72, 0x65,
            0xCB, 0x40, 0x36, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00
        ];

        var result = decoder.Decode(MakeArgsBytes("sensor/msgpack", msgPack));
        result.IsFailure.Should().BeFalse();
        result.DisplayText.Should().Be("""{"temperature":22.5}""");
        result.FormatId.Should().Be("messagepack");
    }

    [Test]
    public void MessagePackDecoder_InvalidPayload_ReturnsHexDumpFallback()
    {
        var decoder = new MessagePackPayloadDecoder();
        // fixstr(3) = 0xA3, followed by "abc" — this is a valid MessagePack
        // string, but MessagePackSerializer.ConvertToJson on a bare string
        // may succeed. Use 0xC1 (never used) which fails ConvertToJson.
        // However, the detector won't match 0xC1 as it's not a structured
        // container. Instead, test the decoder directly with bytes that start
        // with a valid container prefix but have truncated content that
        // fails ConvertToJson while the detector still matches.
        //
        // fixmap(2) = 0x82, then one valid key-value (0x01: 0x02), then
        // truncated: only the key 0x03 without a value.
        byte[] badMsgPack = [0x82, 0x01, 0x02, 0x03];

        var result = decoder.Decode(MakeArgsBytes("sensor/data", badMsgPack));
        result.IsFailure.Should().BeFalse();
        // On ConvertToJson failure, falls back to hex dump.
        result.DisplayText.Should().Be("82010203");
    }

    [Test]
    public void MessagePackDecoder_EmptyPayload_ReturnsEmptyDisplayText()
    {
        var decoder = new MessagePackPayloadDecoder();
        var result = decoder.Decode(MakeArgsBytes("sensor/msgpack", []));
        result.IsFailure.Should().BeFalse();
        result.DisplayText.Should().BeEmpty();
    }

    // --- Sparkplug decoder ---

    [Test]
    public void SparkplugDecoder_EmptyPayload_ReturnsEmptyDisplayText()
    {
        var decoder = new SparkplugPayloadDecoder();
        var result = decoder.Decode(MakeArgs("spBv1.0/group/NBIRTH/eon1", ""));
        result.IsFailure.Should().BeFalse();
        result.DisplayText.Should().BeEmpty();
        result.FormatId.Should().Be("sparkplug-b");
    }

    [Test]
    public void SparkplugDecoder_NoPayload_ReturnsEmptyDisplayText()
    {
        var decoder = new SparkplugPayloadDecoder();
        var result = decoder.Decode(MakeArgsNoPayload("spBv1.0/group/NBIRTH/eon1"));
        result.IsFailure.Should().BeFalse();
        result.DisplayText.Should().BeEmpty();
    }

    [Test]
    public void SparkplugDecoder_ValidProtobuf_SetsTypedPayload()
    {
        var decoder = new SparkplugPayloadDecoder();
        var payload = new Payload
        {
            Timestamp = 1234567890,
            Seq = 1
        };
        var bytes = payload.ToByteArray();
        var result = decoder.Decode(MakeArgsBytes("spBv1.0/group/NBIRTH/eon1", bytes));

        result.IsFailure.Should().BeFalse();
        result.TypedPayload.Should().NotBeNull();
        result.TypedPayload.Should().BeOfType<Payload>();
        ((Payload)result.TypedPayload!).Timestamp.Should().Be(1234567890);
        result.DisplayText.Should().Contain("1234567890");
        result.FormatId.Should().Be("sparkplug-b");
    }

    [Test]
    public void SparkplugDecoder_InvalidProtobuf_DoesNotThrow()
    {
        var decoder = new SparkplugPayloadDecoder();
        var act = () => decoder.Decode(MakeArgs("spBv1.0/group/NDATA/eon1", "\x01\x02\x03invalid"));
        act.Should().NotThrow();
    }

    [Test]
    public void SparkplugDecoder_PlainTextPayload_ReturnsFailureEnvelope()
    {
        // Plain text is not valid protobuf — the decoder returns a failure
        // envelope with IsFailure=true and a descriptive FailureReason.
        var decoder = new SparkplugPayloadDecoder();
        var result = decoder.Decode(MakeArgs("spBv1.0/group/DDATA/eon1", "not protobuf"));
        result.IsFailure.Should().BeTrue();
        result.FailureReason.Should().NotBeNullOrEmpty();
        result.FailureReason.Should().Contain("parse failed");
        result.DisplayText.Should().Contain("Decode failed");
        result.TypedPayload.Should().BeNull();
        result.FormatId.Should().Be("sparkplug-b");
        result.Topic.Should().Be("spBv1.0/group/DDATA/eon1");
    }

    [Test]
    public void SparkplugDecoder_JsonPayload_ReturnsFailureEnvelope()
    {
        // JSON is not valid protobuf — the decoder returns a failure envelope.
        var decoder = new SparkplugPayloadDecoder();
        const string json = """{"timestamp":123,"metrics":[]}""";
        var result = decoder.Decode(MakeArgs("spBv1.0/group/NDATA/eon1", json));
        result.IsFailure.Should().BeTrue();
        result.FailureReason.Should().Contain("parse failed");
        result.DisplayText.Should().Contain("Decode failed");
        result.TypedPayload.Should().BeNull();
        result.FormatId.Should().Be("sparkplug-b");
    }

    [Test]
    public void SparkplugDecoder_InvalidProtobufBytes_ReturnsFailureEnvelope()
    {
        var decoder = new SparkplugPayloadDecoder();
        var result = decoder.Decode(MakeArgs("spBv1.0/group/NDATA/eon1", "\x01\x02\x03invalid"));
        result.IsFailure.Should().BeTrue();
        result.FailureReason.Should().Contain("parse failed");
        result.DisplayText.Should().Contain("Decode failed");
        result.TypedPayload.Should().BeNull();
        result.FormatId.Should().Be("sparkplug-b");
        result.Topic.Should().Be("spBv1.0/group/NDATA/eon1");
    }

    // --- Display parity with old PayloadDecoder.GetPayloadStr ---

    [Test]
    public void DisplayParity_RegularTopic_TextPayload()
    {
        var decoder = new PlainTextPayloadDecoder();
        decoder.Decode(MakeArgs("sensor/temp", "42.5")).DisplayText
            .Should().Be("42.5");
    }

    [Test]
    public void DisplayParity_RegularTopic_JsonPayload()
    {
        var decoder = new JsonPayloadDecoder();
        const string json = """{"temp":21.5}""";
        decoder.Decode(MakeArgs("sensor/data", json)).DisplayText
            .Should().Be(json);
    }

    [Test]
    public void DisplayParity_RegularTopic_EmptyStringPayload()
    {
        var decoder = new EmptyPayloadDecoder();
        decoder.Decode(MakeArgs("sensor/temp", "")).DisplayText
            .Should().BeEmpty();
    }

    [Test]
    public void DisplayParity_RegularTopic_NoPayload()
    {
        var decoder = new EmptyPayloadDecoder();
        decoder.Decode(MakeArgsNoPayload("sensor/temp")).DisplayText
            .Should().BeEmpty();
    }

    [Test]
    public void DisplayParity_XmlPayload()
    {
        var decoder = new XmlPayloadDecoder();
        const string xml = "<root><temp>21.5</temp></root>";
        decoder.Decode(MakeArgs("sensor/data", xml)).DisplayText
            .Should().Be(xml);
    }

    [Test]
    public void DisplayParity_Base64Payload()
    {
        var decoder = new Base64PayloadDecoder();
        const string b64 = "SGVsbG8gV29ybGQ=";
        decoder.Decode(MakeArgs("sensor/data", b64)).DisplayText
            .Should().Be("Hello World");
    }

    [Test]
    public void DisplayParity_HexPayload()
    {
        var decoder = new HexPayloadDecoder();
        const string hex = "4a6f686e";
        decoder.Decode(MakeArgs("sensor/data", hex)).DisplayText
            .Should().Be("John");
    }

    [Test]
    public void DisplayParity_BinaryNonUtf8Payload_ReturnsHexDump()
    {
        var decoder = new BinaryPayloadDecoder();
        byte[] binary = [0x00, 0xFF, 0x01, 0x80, 0xFE];
        decoder.Decode(MakeArgsBytes("sensor/raw", binary)).DisplayText
            .Should().Be("00ff0180fe");
    }

    [Test]
    public void DisplayParity_BinaryValidUtf8Payload_ViaDetectorRoutesToPlainText()
    {
        // In the old architecture, valid UTF-8 bytes on a non-sparkplug topic
        // took the default switch branch (ConvertPayloadToString), returning
        // the UTF-8 string. In the new architecture, the BinaryPayloadDetector
        // requires non-UTF-8 bytes, so valid UTF-8 bytes would be detected as
        // plaintext. This test verifies the BinaryPayloadDecoder itself still
        // hex-dumps any bytes passed to it (consistent with DecodeBinary).
        byte[] utf8 = Encoding.UTF8.GetBytes("hello");

        // Direct BinaryPayloadDecoder always hex-dumps.
        var binaryDecoder = new BinaryPayloadDecoder();
        binaryDecoder.Decode(MakeArgsBytes("sensor/raw", utf8)).DisplayText
            .Should().Be("68656c6c6f");

        // Via registry detection: UTF-8 bytes land on plaintext decoder.
        var registry = BuildRegistry();
        var args = MakeArgsBytes("sensor/raw", utf8);
        var detector = registry.FindDetector(args);
        detector.Should().NotBeNull();
        detector!.FormatId.Should().Be("plaintext");
        var decoder = registry.FindDecoder(detector.FormatId);
        decoder.Should().NotBeNull();
        decoder!.Decode(args).DisplayText.Should().Be("hello");
    }

    [Test]
    public void DisplayParity_MessagePackPayload_ReturnsJson()
    {
        var decoder = new MessagePackPayloadDecoder();
        byte[] msgPack =
        [
            0x81,
            0xAB, 0x74, 0x65, 0x6D, 0x70, 0x65, 0x72, 0x61, 0x74, 0x75, 0x72, 0x65,
            0xCB, 0x40, 0x36, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00
        ];

        decoder.Decode(MakeArgsBytes("sensor/msgpack", msgPack)).DisplayText
            .Should().Be("""{"temperature":22.5}""");
    }

    [Test]
    public void DisplayParity_SparkplugTopic_EmptyPayload_ReturnsEmpty()
    {
        var decoder = new SparkplugPayloadDecoder();
        decoder.Decode(MakeArgs("spBv1.0/group/NBIRTH/eon1", "")).DisplayText
            .Should().BeEmpty();
    }

    [Test]
    public void DisplayParity_SparkplugTopic_NoPayload_ReturnsEmpty()
    {
        var decoder = new SparkplugPayloadDecoder();
        decoder.Decode(MakeArgsNoPayload("spBv1.0/group/NBIRTH/eon1")).DisplayText
            .Should().BeEmpty();
    }

    [Test]
    public void DisplayParity_SparkplugTopic_PlainTextPayload_ReturnsFailureEnvelope()
    {
        var decoder = new SparkplugPayloadDecoder();
        var result = decoder.Decode(MakeArgs("spBv1.0/group/DDATA/eon1", "not protobuf"));
        result.IsFailure.Should().BeTrue();
        result.DisplayText.Should().Contain("Decode failed");
        result.TypedPayload.Should().BeNull();
    }

    [Test]
    public void DisplayParity_SparkplugTopic_JsonPayload_ReturnsFailureEnvelope()
    {
        var decoder = new SparkplugPayloadDecoder();
        const string json = """{"timestamp":123,"metrics":[]}""";
        var result = decoder.Decode(MakeArgs("spBv1.0/group/NDATA/eon1", json));
        result.IsFailure.Should().BeTrue();
        result.DisplayText.Should().Contain("Decode failed");
        result.TypedPayload.Should().BeNull();
    }

    [Test]
    public void DisplayParity_JsonArrayPayload_ReturnsJsonArray()
    {
        var decoder = new JsonPayloadDecoder();
        const string json = "[1,2,3]";
        decoder.Decode(MakeArgs("sensor/data", json)).DisplayText
            .Should().Be(json);
    }

    // --- Envelope field correctness ---

    [Test]
    public void Envelope_Success_HasCorrectFields()
    {
        var decoder = new JsonPayloadDecoder();
        const string json = """{"x":1}""";
        var result = decoder.Decode(MakeArgs("t", json));
        result.FormatId.Should().Be("json");
        result.Topic.Should().Be("t");
        result.RawPayload.Should().Equal(Encoding.UTF8.GetBytes(json));
        result.IsFailure.Should().BeFalse();
        result.FailureReason.Should().BeNull();
        result.TypedPayload.Should().BeNull();
    }

    [Test]
    public void Envelope_SparkplugFailure_InvalidProtobuf_DoesNotThrow()
    {
        // Protobuf is permissive with raw bytes — most sequences parse as a
        // valid message with unknown fields. Verify the decoder does not throw
        // regardless of parse success/failure, matching old decoder behavior.
        var decoder = new SparkplugPayloadDecoder();
        var act = () => decoder.Decode(MakeArgs("spBv1.0/group/NDATA/eon1", "\x01\x02\x03invalid"));
        act.Should().NotThrow();
    }

    // --- HexDump helper parity ---

    [Test]
    public void BinaryPayloadDecoder_HexDump_LowercaseCasing()
    {
        BinaryPayloadDecoder.HexDump([0xAB, 0xCD, 0xEF]).Should().Be("abcdef");
    }

    [Test]
    public void MessagePackPayloadDecoder_HexDump_LowercaseCasing()
    {
        MessagePackPayloadDecoder.HexDump([0xAB, 0xCD, 0xEF]).Should().Be("abcdef");
    }
}
