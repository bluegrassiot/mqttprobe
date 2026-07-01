using System.Text;
using Google.Protobuf;
using MQTTnet;
using MQTTnet.Client;
using MqttProbe.Models.Configuration;
using MqttProbe.Models.Sparkplug;
using MqttProbe.Services.Chart;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Mqtt;
using MqttProbe.Services.Platform;
using MqttProbe.Services.Security;
using MqttProbe.Services.Sparkplug;
using MqttProbe.Services.Telemetry;
using Org.Eclipse.Tahu.Protobuf;

namespace MqttProbe.Shared.Tests.Services.Sparkplug;

[TestFixture]
public class PayloadDecoderTests
{
    private ISparkplugTopologyService _mockTopology = null!;
    private ISettingsStore _mockSettings = null!;
    private PayloadDecoder _decoder = null!;

    [SetUp]
    public void Setup()
    {
        _mockTopology = Substitute.For<ISparkplugTopologyService>();
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup>());
        _mockSettings = Substitute.For<ISettingsStore>();
        _mockSettings.Config.Returns(new AppConfiguration
        {
            Ui = new UiPreferences { EnrichSparkplugAliasNames = true }
        });
        _decoder = new PayloadDecoder(_mockTopology, _mockSettings);
    }

    [TearDown]
    public void TearDown()
    {
        _mockTopology.Dispose();
    }

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
        _decoder.GetPayloadStr(MakeArgs("sensor/temp", "42.5")).Should().Be("42.5");
    }

    [Test]
    public void GetPayloadStr_RegularTopic_JsonPayload_ReturnsJson()
    {
        const string json = """{"temp":21.5}""";
        _decoder.GetPayloadStr(MakeArgs("sensor/data", json)).Should().Be(json);
    }

    [Test]
    public void GetPayloadStr_RegularTopic_EmptyStringPayload_ReturnsEmpty()
    {
        _decoder.GetPayloadStr(MakeArgs("sensor/temp", "")).Should().BeEmpty();
    }

    [Test]
    public void GetPayloadStr_RegularTopic_NoPayload_ReturnsEmpty()
    {
        _decoder.GetPayloadStr(MakeArgsNoPayload("sensor/temp")).Should().BeEmpty();
    }

    [Test]
    public void GetPayloadStr_SparkplugTopic_EmptyPayload_ReturnsEmpty()
    {
        _decoder.GetPayloadStr(MakeArgs("spBv1.0/group/NBIRTH/eon1", "")).Should().BeEmpty();
    }

    [Test]
    public void GetPayloadStr_SparkplugTopic_NoPayload_ReturnsEmpty()
    {
        _decoder.GetPayloadStr(MakeArgsNoPayload("spBv1.0/group/NBIRTH/eon1")).Should().BeEmpty();
    }

    [Test]
    public void GetPayloadStr_SparkplugTopic_PlainTextPayload_FallsBackToUtf8()
    {
        var result = _decoder.GetPayloadStr(MakeArgs("spBv1.0/group/DDATA/eon1", "not protobuf"));
        result.Should().Be("not protobuf");
    }

    [Test]
    public void GetPayloadStr_SparkplugTopic_InvalidProtobufBytes_DoesNotThrow()
    {
        var act = () => _decoder.GetPayloadStr(MakeArgs("spBv1.0/group/NDATA/eon1", "\x01\x02\x03invalid"));
        act.Should().NotThrow();
    }

    [Test]
    public void GetPayloadStr_SparkplugTopic_JsonPayload_FallsBackToUtf8()
    {
        const string json = """{"timestamp":123,"metrics":[]}""";
        var result = _decoder.GetPayloadStr(MakeArgs("spBv1.0/group/NDATA/eon1", json));
        result.Should().Be(json);
    }

    [Test]
    public void GetPayloadStr_TopicNotSpBv10_UsesUtf8Directly()
    {
        _decoder.GetPayloadStr(MakeArgs("spBv2.0/group/NBIRTH/eon1", "payload"))
            .Should().Be("payload");
    }

    [Test]
    public void GetPayloadStr_XmlPayload_ReturnsXmlString()
    {
        const string xml = "<root><temp>21.5</temp></root>";
        _decoder.GetPayloadStr(MakeArgs("sensor/data", xml)).Should().Be(xml);
    }

    [Test]
    public void GetPayloadStr_Base64Payload_ReturnsBase64String()
    {
        const string b64 = "SGVsbG8gV29ybGQ=";
        _decoder.GetPayloadStr(MakeArgs("sensor/data", b64)).Should().Be(b64);
    }

    [Test]
    public void GetPayloadStr_HexPayload_ReturnsHexString()
    {
        const string hex = "4a6f686e";
        _decoder.GetPayloadStr(MakeArgs("sensor/data", hex)).Should().Be(hex);
    }

    [Test]
    public void GetPayloadStr_BinaryNonUtf8Payload_ReturnsHexDump()
    {
        byte[] binary = [0x00, 0xFF, 0x01, 0x80, 0xFE];
        _decoder.GetPayloadStr(MakeArgsBytes("sensor/raw", binary))
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
        _decoder.GetPayloadStr(MakeArgsBytes("sensor/msgpack", msgPack))
            .Should().Be("""{"temperature":22.5}""");
    }

    [Test]
    public void GetPayloadStr_BinaryValidUtf8Payload_ReturnsUtf8String()
    {
        byte[] utf8 = Encoding.UTF8.GetBytes("hello");
        _decoder.GetPayloadStr(MakeArgsBytes("sensor/raw", utf8))
            .Should().Be("hello");
    }

    [Test]
    public void GetPayloadStr_JsonArrayPayload_ReturnsJsonArray()
    {
        const string json = "[1,2,3]";
        _decoder.GetPayloadStr(MakeArgs("sensor/data", json)).Should().Be(json);
    }

    // --- Alias resolution tests ---

    private static byte[] BuildSparkplugPayload(string? name, ulong alias, double doubleValue)
    {
        var payload = new Payload { Timestamp = 1234567890 };
        var metric = new Payload.Types.Metric
        {
            Datatype = 10, // double
            DoubleValue = doubleValue
        };
        if (name is not null)
            metric.Name = name;
        if (alias != 0)
            metric.Alias = alias;
        payload.Metrics.Add(metric);
        return payload.ToByteArray();
    }

    [Test]
    public void Decode_Sparkplug_AliasOnlyMetric_ResolvesName()
    {
        var node = new SpbNode { NodeId = "eon1", GroupId = "group" };
        node.AliasMap[42] = "Flow Rate";
        var group = new SpbGroup { GroupId = "group" };
        group.Nodes["eon1"] = node;
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup> { ["group"] = group });

        var bytes = BuildSparkplugPayload(null, 42, 3.14);
        var args = MakeArgsBytes("spBv1.0/group/NDATA/eon1", bytes);
        var result = _decoder.Decode(args);

        result.AliasNames.Should().NotBeNull();
        result.AliasNames![42].Should().Be("Flow Rate");
    }

    [Test]
    public void Decode_Sparkplug_SettingOff_ReturnsNullAliasNames()
    {
        _mockSettings.Config.Returns(new AppConfiguration
        {
            Ui = new UiPreferences { EnrichSparkplugAliasNames = false }
        });
        _decoder = new PayloadDecoder(_mockTopology, _mockSettings);

        var node = new SpbNode { NodeId = "eon1", GroupId = "group" };
        node.AliasMap[42] = "Flow Rate";
        var group = new SpbGroup { GroupId = "group" };
        group.Nodes["eon1"] = node;
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup> { ["group"] = group });

        var bytes = BuildSparkplugPayload(null, 42, 3.14);
        var args = MakeArgsBytes("spBv1.0/group/NDATA/eon1", bytes);
        var result = _decoder.Decode(args);

        result.AliasNames.Should().BeNull();
    }

    [Test]
    public void Decode_Sparkplug_MissingAliasMapping_OmitsMetric()
    {
        var node = new SpbNode { NodeId = "eon1", GroupId = "group" };
        node.AliasMap[7] = "Temperature";
        var group = new SpbGroup { GroupId = "group" };
        group.Nodes["eon1"] = node;
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup> { ["group"] = group });

        var bytes = BuildSparkplugPayload(null, 99, 1.0);
        var args = MakeArgsBytes("spBv1.0/group/NDATA/eon1", bytes);
        var result = _decoder.Decode(args);

        result.AliasNames.Should().BeNull();
    }

    [Test]
    public void Decode_Sparkplug_NamedMetric_NotIncludedInAliasNames()
    {
        var node = new SpbNode { NodeId = "eon1", GroupId = "group" };
        node.AliasMap[5] = "Pressure";
        var group = new SpbGroup { GroupId = "group" };
        group.Nodes["eon1"] = node;
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup> { ["group"] = group });

        var bytes = BuildSparkplugPayload("Pressure", 5, 1013.25);
        var args = MakeArgsBytes("spBv1.0/group/NBIRTH/eon1", bytes);
        var result = _decoder.Decode(args);

        result.AliasNames.Should().BeNull();
    }

    [Test]
    public void Decode_NonSparkplug_ReturnsNullAliasNames()
    {
        var args = MakeArgs("sensor/temp", "42.5");
        var result = _decoder.Decode(args);

        result.AliasNames.Should().BeNull();
    }

    [Test]
    public void Decode_Sparkplug_TopologyNotInitialized_ReturnsNullAliasNames()
    {
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup>());

        var bytes = BuildSparkplugPayload(null, 42, 3.14);
        var args = MakeArgsBytes("spBv1.0/group/NDATA/eon1", bytes);
        var result = _decoder.Decode(args);

        result.AliasNames.Should().BeNull();
    }
}
