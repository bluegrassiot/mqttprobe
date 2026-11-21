using MQTTnet;
using MqttProbe.Core.Services.Plugins.BuiltIn;

namespace MqttProbe.UI.Tests.Components.Browser;

[TestFixture]
public class CborPayloadDisplayTests
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

    [Test]
    public void CborMap_DisplayTextUsesJsonTree()
    {
        var envelope = new CborPayloadDecoder().Decode(
            MakeArgs([0xA2, 0x61, 0x61, 0x01, 0x61, 0x62, 0x82, 0x02, 0x03]));

        PayloadBrowser.GetPayloadType(envelope.DisplayText).Should().Be(PayloadBrowser.PayloadType.Json);
        JsonTreeView.IsExpandableJson(envelope.DisplayText).Should().BeTrue();
    }

    [Test]
    public void CborArray_DisplayTextUsesJsonTree()
    {
        var envelope = new CborPayloadDecoder().Decode(MakeArgs([0x82, 0x01, 0x02]));

        PayloadBrowser.GetPayloadType(envelope.DisplayText).Should().Be(PayloadBrowser.PayloadType.Json);
        JsonTreeView.IsExpandableJson(envelope.DisplayText).Should().BeTrue();
    }
}
