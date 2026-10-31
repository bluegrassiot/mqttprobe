using MQTTnet;
using MqttProbe.Core.Services.Plugins.BuiltIn;
using MqttProbe.Core.Services.Plugins.Registry;

namespace MqttProbe.Core.Tests.Services.Plugins.BuiltIn;

[TestFixture]
public class CborPipelineTests
{
    private static MqttApplicationMessageReceivedEventArgs MakeArgs(byte[] payload)
    {
        var appMsg = new MqttApplicationMessageBuilder()
            .WithTopic("sensor/cbor")
            .WithPayload(payload)
            .Build();
        var packet = new MQTTnet.Packets.MqttPublishPacket { Topic = "sensor/cbor" };
        return new MqttApplicationMessageReceivedEventArgs("test-client", appMsg, packet, null);
    }

    private static PluginRegistry BuildRegistry()
    {
        var builder = new PluginRegistryBuilder();
        BuiltInPluginRegistration.RegisterBuiltIns(builder);
        return builder.Build();
    }

    [Test]
    public void Registry_DetectsAndDecodesCborMap()
    {
        var registry = BuildRegistry();
        var args = MakeArgs([0xA1, 0x61, 0x61, 0x01]);

        var detector = registry.FindDetector(args);

        detector.Should().NotBeNull();
        detector.FormatId.Should().Be("cbor");
        detector.DisplayName.Should().Be("CBOR");
        registry.FindDecoder(detector.FormatId)!.Decode(args).DisplayText.Should().Be("""{"a":1}""");
    }

    [Test]
    public void Registry_KeepsAmbiguousEmptyMapAsMessagePack()
    {
        var registry = BuildRegistry();
        var detector = registry.FindDetector(MakeArgs([0x80]));

        detector.Should().NotBeNull();
        detector.FormatId.Should().Be("messagepack");
    }

    [Test]
    public void Registry_AsciiTextThatIsSyntacticallyCbor_IsNotDetectedAsCbor()
    {
        var registry = BuildRegistry();
        var detector = registry.FindDetector(MakeArgs(System.Text.Encoding.ASCII.GetBytes("a1")));

        detector.Should().NotBeNull();
        detector.FormatId.Should().NotBe("cbor");
    }

    [Test]
    public void Registry_DetectsAndDecodesBinaryScalarAsCbor()
    {
        var registry = BuildRegistry();
        var args = MakeArgs([0x19, 0x03, 0xE8]);

        var detector = registry.FindDetector(args);

        detector.Should().NotBeNull();
        detector.FormatId.Should().Be("cbor");
        registry.FindDecoder(detector.FormatId)!.Decode(args).DisplayText.Should().Be("1000");
    }
}
