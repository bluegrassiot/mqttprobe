using MQTTnet;
using MqttProbe.Core.Services.Plugins.BuiltIn;

namespace MqttProbe.Core.Tests.Services.Plugins.BuiltIn;

[TestFixture]
public class CborPayloadDetectorTests
{
    private static MqttApplicationMessageReceivedEventArgs MakeArgs(byte[] payload)
    {
        var appMsg = new MqttApplicationMessageBuilder()
            .WithTopic("sensor/data")
            .WithPayload(payload)
            .Build();
        var packet = new MQTTnet.Packets.MqttPublishPacket { Topic = "sensor/data" };
        return new MqttApplicationMessageReceivedEventArgs("test-client", appMsg, packet, null);
    }

    [Test]
    public void CanDetect_MapPayload_ReturnsTrue()
    {
        var detector = new CborPayloadDetector();

        detector.CanDetect(MakeArgs([0xA1, 0x61, 0x61, 0x01])).Should().BeTrue();
    }

    [Test]
    public void CanDetect_MapWithInvalidUtf8Text_ReturnsFalse()
    {
        var detector = new CborPayloadDetector();

        detector.CanDetect(MakeArgs([0xA1, 0x61, 0xFF, 0x01])).Should().BeFalse();
    }

    [Test]
    public void CanDetect_TextPayload_ReturnsFalse()
    {
        var detector = new CborPayloadDetector();

        detector.CanDetect(MakeArgs(System.Text.Encoding.ASCII.GetBytes("a1"))).Should().BeFalse();
    }

    [Test]
    public void CanDetect_EmptyPayload_ReturnsFalse()
    {
        new CborPayloadDetector().CanDetect(MakeArgs([])).Should().BeFalse();
    }

    [Test]
    public void CanDetect_TruncatedPayload_ReturnsFalse()
    {
        new CborPayloadDetector().CanDetect(MakeArgs([0xA2, 0x61, 0x61, 0x01])).Should().BeFalse();
    }

    [Test]
    public void CanDetect_TrailingGarbage_ReturnsFalse()
    {
        new CborPayloadDetector().CanDetect(MakeArgs([0xA1, 0x61, 0x61, 0x01, 0x00])).Should().BeFalse();
    }

    [Test]
    public void CanDetect_ByteStringPayload_ReturnsTrue()
    {
        new CborPayloadDetector().CanDetect(MakeArgs([0x43, 0x01, 0x02, 0x03])).Should().BeTrue();
    }

    [Test]
    public void CanDetect_EmptyByteString_ReturnsFalse()
    {
        new CborPayloadDetector().CanDetect(MakeArgs([0x40])).Should().BeFalse();
    }

    [Test]
    public void CanDetect_PrintableAsciiByteString_ReturnsFalse()
    {
        new CborPayloadDetector()
            .CanDetect(MakeArgs(System.Text.Encoding.ASCII.GetBytes("A1"))).Should().BeFalse();
    }

    [Test]
    public void CanDetect_ArrayPayload_ReturnsTrue()
    {
        new CborPayloadDetector().CanDetect(MakeArgs([0x82, 0x01, 0x02])).Should().BeTrue();
    }

    [Test]
    public void CanDetect_SelfDescribedPayload_ReturnsTrue()
    {
        new CborPayloadDetector()
            .CanDetect(MakeArgs([0xD9, 0xD9, 0xF7, 0x82, 0x01, 0x02])).Should().BeTrue();
    }

    [Test]
    public void CanDetect_ArbitraryBytes_DoesNotThrow()
    {
        var act = () => new CborPayloadDetector().CanDetect(MakeArgs([0xFF, 0xFE, 0x00, 0x9F]));

        act.Should().NotThrow();
        act().Should().BeFalse();
    }

    [Test]
    public void CanDetect_UnsignedIntegerPayload_ReturnsTrue()
    {
        new CborPayloadDetector().CanDetect(MakeArgs([0x19, 0x03, 0xE8])).Should().BeTrue();
    }

    [Test]
    public void CanDetect_NegativeIntegerPayload_ReturnsTrue()
    {
        new CborPayloadDetector().CanDetect(MakeArgs([0x39, 0x03, 0xE7])).Should().BeTrue();
    }

    [Test]
    public void CanDetect_FloatPayload_ReturnsTrue()
    {
        new CborPayloadDetector().CanDetect(MakeArgs([0xF9, 0x3E, 0x00])).Should().BeTrue();
    }

    [Test]
    public void CanDetect_BooleanPayload_ReturnsTrue()
    {
        new CborPayloadDetector().CanDetect(MakeArgs([0xF4])).Should().BeTrue();
    }

    [Test]
    public void CanDetect_PrintableSingleByteScalar_ReturnsFalse()
    {
        new CborPayloadDetector().CanDetect(MakeArgs([0x20])).Should().BeFalse();
    }

    [Test]
    public void CanDetect_TextRootWithNonPrintableContent_ReturnsFalse()
    {
        new CborPayloadDetector().CanDetect(MakeArgs([0x62, 0x00, 0x01])).Should().BeFalse();
    }
}
