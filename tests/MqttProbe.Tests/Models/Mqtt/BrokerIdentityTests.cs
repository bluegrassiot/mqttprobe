using MqttProbe.Models.Mqtt;

namespace MqttProbe.Shared.Tests.Models.Mqtt;

[TestFixture]
public class BrokerIdentityTests
{
    [Test]
    public void FromConnection_ExtractsHostPortProtocolWebsocketBasePath()
    {
        var conn = new Connection
        {
            Host = "broker.example.com",
            Port = 8883,
            Protocol = Protocol.Mqtt,
            UseTls = true,
            WebsocketBasePath = "mqtt"
        };

        var identity = BrokerIdentity.FromConnection(conn);

        identity.Host.Should().Be("broker.example.com");
        identity.Port.Should().Be(8883);
        identity.Protocol.Should().Be(Protocol.Mqtt);
        identity.UseTls.Should().BeTrue();
        identity.WebsocketBasePath.Should().Be("/mqtt");
    }

    [Test]
    public void FromConnection_NormalizesHostToLowerCase()
    {
        var conn = new Connection { Host = "Broker.EXAMPLE.COM", Port = 1883, Protocol = Protocol.Mqtt };

        var identity = BrokerIdentity.FromConnection(conn);

        identity.Host.Should().Be("broker.example.com");
    }

    [Test]
    public void FromConnection_NormalizesWebsocketBasePath_AddsLeadingSlash()
    {
        var conn = new Connection
        {
            Host = "host",
            Port = 1883,
            Protocol = Protocol.WebSocket,
            WebsocketBasePath = "ws/mqtt"
        };

        var identity = BrokerIdentity.FromConnection(conn);

        identity.WebsocketBasePath.Should().Be("/ws/mqtt");
    }

    [Test]
    public void FromConnection_WebsocketBasePathAlreadyHasSlash_Preserves()
    {
        var conn = new Connection
        {
            Host = "host",
            Port = 1883,
            Protocol = Protocol.WebSocket,
            WebsocketBasePath = "/ws/mqtt"
        };

        var identity = BrokerIdentity.FromConnection(conn);

        identity.WebsocketBasePath.Should().Be("/ws/mqtt");
    }

    [Test]
    public void FromConnection_NullOrEmptyWebsocketBasePath_NormalizesToNull()
    {
        var connNull = new Connection
        {
            Host = "host",
            Port = 1883,
            Protocol = Protocol.WebSocket,
            WebsocketBasePath = null!
        };
        var connEmpty = new Connection
        {
            Host = "host",
            Port = 1883,
            Protocol = Protocol.WebSocket,
            WebsocketBasePath = ""
        };
        var connWhitespace = new Connection
        {
            Host = "host",
            Port = 1883,
            Protocol = Protocol.WebSocket,
            WebsocketBasePath = "   "
        };

        BrokerIdentity.FromConnection(connNull).WebsocketBasePath.Should().BeNull();
        BrokerIdentity.FromConnection(connEmpty).WebsocketBasePath.Should().BeNull();
        BrokerIdentity.FromConnection(connWhitespace).WebsocketBasePath.Should().BeNull();
    }

    [Test]
    public void FromConnection_DifferentClientIdProducesSameIdentity()
    {
        var connA = new Connection
        {
            Host = "broker.local",
            Port = 1883,
            Protocol = Protocol.Mqtt,
            ClientId = "cid-1"
        };
        var connB = new Connection
        {
            Host = "broker.local",
            Port = 1883,
            Protocol = Protocol.Mqtt,
            ClientId = "cid-2"
        };

        var identityA = BrokerIdentity.FromConnection(connA);
        var identityB = BrokerIdentity.FromConnection(connB);

        identityA.Should().Be(identityB);
    }

    [Test]
    public void Equals_SameFields_ReturnsTrue()
    {
        var a = new BrokerIdentity("host", 1883, Protocol.Mqtt, false, "/mqtt");
        var b = new BrokerIdentity("host", 1883, Protocol.Mqtt, false, "/mqtt");

        a.Should().Be(b);
        (a == b).Should().BeTrue();
    }

    [Test]
    public void Equals_DifferentHost_ReturnsFalse()
    {
        var a = new BrokerIdentity("host-a", 1883, Protocol.Mqtt, false, "/mqtt");
        var b = new BrokerIdentity("host-b", 1883, Protocol.Mqtt, false, "/mqtt");

        a.Should().NotBe(b);
    }

    [Test]
    public void Equals_DifferentPort_ReturnsFalse()
    {
        var a = new BrokerIdentity("host", 1883, Protocol.Mqtt, false, "/mqtt");
        var b = new BrokerIdentity("host", 8883, Protocol.Mqtt, false, "/mqtt");

        a.Should().NotBe(b);
    }

    [Test]
    public void Equals_DifferentProtocol_ReturnsFalse()
    {
        var a = new BrokerIdentity("host", 1883, Protocol.Mqtt, false, null);
        var b = new BrokerIdentity("host", 1883, Protocol.WebSocket, false, "/mqtt");

        a.Should().NotBe(b);
    }

    [Test]
    public void Equals_DifferentUseTls_ReturnsFalse()
    {
        var a = new BrokerIdentity("host", 1883, Protocol.Mqtt, false, "/mqtt");
        var b = new BrokerIdentity("host", 1883, Protocol.Mqtt, true, "/mqtt");

        a.Should().NotBe(b);
    }

    [Test]
    public void Equals_DifferentClientId_ReturnsTrue()
    {
        // ClientId is NOT part of broker identity — same broker, different session
        var connA = new Connection
        {
            Host = "host",
            Port = 1883,
            Protocol = Protocol.Mqtt,
            ClientId = "cid-1"
        };
        var connB = new Connection
        {
            Host = "host",
            Port = 1883,
            Protocol = Protocol.Mqtt,
            ClientId = "cid-2"
        };

        var a = BrokerIdentity.FromConnection(connA);
        var b = BrokerIdentity.FromConnection(connB);

        a.Should().Be(b);
    }

    [Test]
    public void Equals_DifferentWebsocketBasePath_ReturnsFalse()
    {
        var a = new BrokerIdentity("host", 8083, Protocol.WebSocket, false, "/mqtt");
        var b = new BrokerIdentity("host", 8083, Protocol.WebSocket, false, "/ws");

        a.Should().NotBe(b);
    }

    [Test]
    public void Equals_SameTlsDifferentProtocol_MqttTlsVsWsNoTls_ReturnsFalse()
    {
        // mqtt+tls (mqtts) vs ws (no tls) — different broker identity
        var a = new BrokerIdentity("host", 8883, Protocol.Mqtt, true, null);
        var b = new BrokerIdentity("host", 8083, Protocol.WebSocket, false, "/mqtt");

        a.Should().NotBe(b);
    }

    [Test]
    public void Equals_WebSocketVsMqttWithNullPath_Differs()
    {
        var a = new BrokerIdentity("host", 8083, Protocol.WebSocket, false, null);
        var b = new BrokerIdentity("host", 8083, Protocol.WebSocket, false, "/mqtt");

        a.Should().NotBe(b);
    }

    [Test]
    public void GetHashCode_SameFields_ReturnsSameHash()
    {
        var a = new BrokerIdentity("host", 1883, Protocol.Mqtt, false, "/mqtt");
        var b = new BrokerIdentity("host", 1883, Protocol.Mqtt, false, "/mqtt");

        a.GetHashCode().Should().Be(b.GetHashCode());
    }
}
