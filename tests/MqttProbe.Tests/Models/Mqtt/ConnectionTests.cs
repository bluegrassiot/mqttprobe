using MqttProbe.Models.Chart;
using MqttProbe.Models.Configuration;
using MqttProbe.Models.Mqtt;
using MqttProbe.Models.Sparkplug;

namespace MqttProbe.Shared.Tests.Models.Mqtt;

[TestFixture]
public class ConnectionTests
{
    [Test]
    public void Constructor_SetsDefaultPropertiesCorrectly()
    {
        var conn = new Connection();

        conn.Name.Should().Be("New Connection");
        conn.Port.Should().Be(1883);
        conn.Protocol.Should().Be(Protocol.Mqtt);
        conn.UseTls.Should().BeFalse();
        conn.AllowUntrustedCertificate.Should().BeFalse();
        conn.WebsocketBasePath.Should().Be("mqtt");
        conn.ClientId.Should().StartWith("mqttprobe_");
    }

    [Test]
    public void Equals_ReturnsTrueForIdenticalConnections()
    {
        var a = new Connection { Name = "Test", Host = "broker.local", Port = 1883, ClientId = "same-id" };
        var b = new Connection { Name = "Test", Host = "broker.local", Port = 1883, ClientId = "same-id" };

        a.Equals(b).Should().BeTrue();
    }

    [Test]
    public void Equals_ReturnsFalseWhenNameDiffers()
    {
        var a = new Connection { Name = "A", ClientId = "same-id" };
        var b = new Connection { Name = "B", ClientId = "same-id" };

        a.Equals(b).Should().BeFalse();
    }

    [Test]
    public void Equals_ReturnsFalseWhenHostDiffers()
    {
        var a = new Connection { Name = "Test", Host = "broker-a.local", ClientId = "same-id" };
        var b = new Connection { Name = "Test", Host = "broker-b.local", ClientId = "same-id" };

        a.Equals(b).Should().BeFalse();
    }

    [Test]
    public void Equals_ReturnsFalseWhenPortDiffers()
    {
        var a = new Connection { Name = "Test", Port = 1883, ClientId = "same-id" };
        var b = new Connection { Name = "Test", Port = 8883, ClientId = "same-id" };

        a.Equals(b).Should().BeFalse();
    }

    [Test]
    public void CloneWithoutPassword_ReturnsCopyWithNullPassword()
    {
        var original = new Connection { Name = "Test", Password = "secret" };

        var clone = original.CloneWithoutPassword();

        clone.Password.Should().BeNull();
    }

    [Test]
    public void CloneWithoutPassword_DoesNotMutateOriginal()
    {
        var original = new Connection { Password = "secret" };

        _ = original.CloneWithoutPassword();

        original.Password.Should().Be("secret");
    }

    [Test]
    public void Id_IsNonEmptyGuid_OnConstruction()
    {
        var conn = new Connection();

        conn.Id.Should().NotBe(Guid.Empty);
    }

    [Test]
    public void Id_IsExcludedFromEquality_TwoConnectionsWithSameFieldsAreEqual()
    {
        var a = new Connection { Name = "Test", Host = "broker.local", Port = 1883, ClientId = "same-id" };
        var b = new Connection { Name = "Test", Host = "broker.local", Port = 1883, ClientId = "same-id" };

        a.Id.Should().NotBe(b.Id);
        a.Equals(b).Should().BeTrue();
    }

    [Test]
    public void Equals_ReturnsFalseWhenAllowUntrustedCertificateDiffers()
    {
        var trusted = new Connection { Name = "Test", Host = "broker.local", ClientId = "same-id", AllowUntrustedCertificate = false };
        var untrusted = new Connection { Name = "Test", Host = "broker.local", ClientId = "same-id", AllowUntrustedCertificate = true };

        trusted.Equals(untrusted).Should().BeFalse();
    }

    [Test]
    public void GetHashCode_DiffersWhenAllowUntrustedCertificateDiffers()
    {
        var trusted = new Connection { Name = "Test", Host = "broker.local", ClientId = "same-id", AllowUntrustedCertificate = false };
        var untrusted = new Connection { Name = "Test", Host = "broker.local", ClientId = "same-id", AllowUntrustedCertificate = true };

        trusted.GetHashCode().Should().NotBe(untrusted.GetHashCode());
    }

    [Test]
    public void CloneWithoutPassword_PreservesAllOtherFields()
    {
        var original = new Connection
        {
            Name = "Broker",
            Host = "mqtt.example.com",
            Port = 8883,
            User = "admin",
            Password = "secret",
            Protocol = Protocol.WebSocket,
            UseTls = true,
            AllowUntrustedCertificate = true,
            WebsocketBasePath = "ws",
            ClientId = "my-client"
        };

        var clone = original.CloneWithoutPassword();

        clone.Name.Should().Be("Broker");
        clone.Host.Should().Be("mqtt.example.com");
        clone.Port.Should().Be(8883);
        clone.User.Should().Be("admin");
        clone.Protocol.Should().Be(Protocol.WebSocket);
        clone.UseTls.Should().BeTrue();
        clone.AllowUntrustedCertificate.Should().BeTrue();
        clone.WebsocketBasePath.Should().Be("ws");
        clone.ClientId.Should().Be("my-client");
    }
}
