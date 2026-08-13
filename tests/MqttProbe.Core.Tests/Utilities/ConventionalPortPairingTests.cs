using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Utilities;

namespace MqttProbe.Core.Tests.Utilities;

[TestFixture]
public class ConventionalPortPairingTests
{
    [Test]
    public void Mqtt_PlainPort_TlsOn_SwapsToTlsPort() => ConventionalPortPairing.ApplyForTlsChange(Protocol.Mqtt, 1883, true).Should().Be(8883);

    [Test]
    public void Mqtt_TlsPort_TlsOff_SwapsToPlainPort() => ConventionalPortPairing.ApplyForTlsChange(Protocol.Mqtt, 8883, false).Should().Be(1883);

    [Test]
    public void Mqtt_TlsPort_TlsOn_NoChange() => ConventionalPortPairing.ApplyForTlsChange(Protocol.Mqtt, 8883, true).Should().Be(8883);

    [Test]
    public void Mqtt_PlainPort_TlsOff_NoChange() => ConventionalPortPairing.ApplyForTlsChange(Protocol.Mqtt, 1883, false).Should().Be(1883);

    [Test]
    public void Mqtt_CustomPort_TlsOn_Unchanged() => ConventionalPortPairing.ApplyForTlsChange(Protocol.Mqtt, 8884, true).Should().Be(8884);

    [Test]
    public void Mqtt_CustomPort_TlsOff_Unchanged() => ConventionalPortPairing.ApplyForTlsChange(Protocol.Mqtt, 8884, false).Should().Be(8884);

    [Test]
    public void WebSocket_PlainPort_TlsOn_SwapsToTlsPort() => ConventionalPortPairing.ApplyForTlsChange(Protocol.WebSocket, 8083, true).Should().Be(8084);

    [Test]
    public void WebSocket_TlsPort_TlsOff_SwapsToPlainPort() => ConventionalPortPairing.ApplyForTlsChange(Protocol.WebSocket, 8084, false).Should().Be(8083);

    [Test]
    public void WebSocket_CustomPort_Unchanged() => ConventionalPortPairing.ApplyForTlsChange(Protocol.WebSocket, 8081, true).Should().Be(8081);

    [Test]
    public void Mqtt_PortOnWrongProtocol_Unchanged() => ConventionalPortPairing.ApplyForTlsChange(Protocol.WebSocket, 1883, true).Should().Be(1883);

    [Test]
    public void ProtocolChange_Mqtt1883_Plain_ToWebSocket_Returns8083() =>
        ConventionalPortPairing.ApplyForProtocolChange(Protocol.WebSocket, 1883, false).Should().Be(8083);

    [Test]
    public void ProtocolChange_Ws8083_Plain_ToMqtt_Returns1883() =>
        ConventionalPortPairing.ApplyForProtocolChange(Protocol.Mqtt, 8083, false).Should().Be(1883);

    [Test]
    public void ProtocolChange_Mqtt8883_Tls_ToWebSocket_Returns8084() =>
        ConventionalPortPairing.ApplyForProtocolChange(Protocol.WebSocket, 8883, true).Should().Be(8084);

    [Test]
    public void ProtocolChange_Ws8084_Tls_ToMqtt_Returns8883() =>
        ConventionalPortPairing.ApplyForProtocolChange(Protocol.Mqtt, 8084, true).Should().Be(8883);

    [Test]
    public void ProtocolChange_Custom8884_Plain_UnchangedBothDirections()
    {
        ConventionalPortPairing.ApplyForProtocolChange(Protocol.WebSocket, 8884, false).Should().Be(8884);
        ConventionalPortPairing.ApplyForProtocolChange(Protocol.Mqtt, 8884, false).Should().Be(8884);
    }

    [Test]
    public void ProtocolChange_Custom8081_Tls_UnchangedBothDirections()
    {
        ConventionalPortPairing.ApplyForProtocolChange(Protocol.WebSocket, 8081, true).Should().Be(8081);
        ConventionalPortPairing.ApplyForProtocolChange(Protocol.Mqtt, 8081, true).Should().Be(8081);
    }

    [Test]
    public void ProtocolChange_1883_TlsOn_SwitchToWebSocket_Unchanged() =>
        ConventionalPortPairing.ApplyForProtocolChange(Protocol.WebSocket, 1883, true).Should().Be(1883);

    [Test]
    public void ProtocolChange_8083_Plain_SwitchToWebSocket_AlreadyCorrect_Unchanged() =>
        ConventionalPortPairing.ApplyForProtocolChange(Protocol.WebSocket, 8083, false).Should().Be(8083);
}
