using MqttProbe.Core.Models.Mqtt;

namespace MqttProbe.Core.Tests.Models.Mqtt;

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

    [Test]
    public void Equals_ConsidersClientCertificateAssetId()
    {
        var a = new Connection
        {
            Name = "Test",
            Host = "broker.local",
            Port = 1883,
            User = "u",
            Password = "p",
            Protocol = Protocol.Mqtt,
            MqttVersion = MqttVersion.V311,
            ClientId = "fixed-id",
            WebsocketBasePath = "mqtt",
            UseTls = true,
            AllowUntrustedCertificate = false,
            ConnectTimeout = 15,
            ClientCertificateAssetId = "abc"
        };
        var b = a.Clone();
        var c = a.Clone();
        c.ClientCertificateAssetId = "xyz";

        a.Equals(b).Should().BeTrue();
        a.Equals(c).Should().BeFalse();
    }

    [Test]
    public void Equals_NullClientCertificateAssetId_BackwardCompatible()
    {
        var a = new Connection { ClientCertificateAssetId = null, ClientId = "fixed" };
        var b = a.Clone();
        b.ClientCertificateAssetId = null;
        a.Equals(b).Should().BeTrue();
    }

    [Test]
    public void Clone_PreservesClientCertificateAssetId()
    {
        var conn = new Connection { ClientCertificateAssetId = "abc" };
        var clone = conn.Clone();
        clone.ClientCertificateAssetId.Should().Be("abc");
    }

    [Test]
    public void CloneWithoutPassword_PreservesClientCertificateAssetId()
    {
        var conn = new Connection { ClientCertificateAssetId = "abc", Password = "secret" };
        var clone = conn.CloneWithoutPassword();
        clone.ClientCertificateAssetId.Should().Be("abc");
        clone.Password.Should().BeNull();
    }

    [Test]
    public void Clone_DeepCopiesTopicExcludes()
    {
        var original = new Connection { TopicExcludes = ["sensors/#"] };

        var clone = original.Clone();
        clone.TopicExcludes.Add("other");

        original.TopicExcludes.Should().Equal("sensors/#");
        clone.TopicExcludes.Should().Equal("sensors/#", "other");
    }

    [Test]
    public void Equals_ConsidersTopicExcludes()
    {
        var a = new Connection { ClientId = "same", TopicExcludes = ["sensors/#"] };
        var b = a.Clone();
        b.TopicExcludes = ["other/#"];

        a.Equals(b).Should().BeFalse();
        a.GetHashCode().Should().NotBe(b.GetHashCode());
    }

    [Test]
    public void JsonRoundTrip_PreservesTopicExcludes()
    {
        var options = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        };

        var original = new Connection { TopicExcludes = ["#", "devices/+/debug"] };
        var json = System.Text.Json.JsonSerializer.Serialize(original, options);
        var restored = System.Text.Json.JsonSerializer.Deserialize<Connection>(json, options);

        restored.Should().NotBeNull();
        restored.TopicExcludes.Should().Equal("#", "devices/+/debug");
    }

    [Test]
    public void Constructor_SetsSessionTimingDefaults()
    {
        var conn = new Connection();

        conn.ConnectTimeout.Should().Be(15);
        conn.ReconnectDelay.Should().Be(5);
        conn.KeepAlivePeriod.Should().Be(15);
    }

    [Test]
    public void Constructor_DefaultCleanStartIsTrue()
    {
        var conn = new Connection();

        conn.CleanStart.Should().BeTrue();
    }

    [Test]
    public void Constructor_DefaultSessionExpiryIntervalSecondsIs3600()
    {
        var conn = new Connection();

        conn.SessionExpiryIntervalSeconds.Should().Be(3600u);
    }

    [Test]
    public void Equals_ReturnsFalseWhenCleanStartDiffers()
    {
        var a = new Connection { Name = "Test", Host = "broker.local", ClientId = "same-id", CleanStart = true };
        var b = new Connection { Name = "Test", Host = "broker.local", ClientId = "same-id", CleanStart = false };

        a.Equals(b).Should().BeFalse();
    }

    [Test]
    public void GetHashCode_DiffersWhenCleanStartDiffers()
    {
        var a = new Connection { Name = "Test", Host = "broker.local", ClientId = "same-id", CleanStart = true };
        var b = new Connection { Name = "Test", Host = "broker.local", ClientId = "same-id", CleanStart = false };

        a.GetHashCode().Should().NotBe(b.GetHashCode());
    }

    [Test]
    public void Equals_ReturnsFalseWhenSessionExpiryIntervalSecondsDiffers()
    {
        var a = new Connection { Name = "Test", Host = "broker.local", ClientId = "same-id", SessionExpiryIntervalSeconds = 3600 };
        var b = new Connection { Name = "Test", Host = "broker.local", ClientId = "same-id", SessionExpiryIntervalSeconds = 7200 };

        a.Equals(b).Should().BeFalse();
    }

    [Test]
    public void GetHashCode_DiffersWhenSessionExpiryIntervalSecondsDiffers()
    {
        var a = new Connection { Name = "Test", Host = "broker.local", ClientId = "same-id", SessionExpiryIntervalSeconds = 3600 };
        var b = new Connection { Name = "Test", Host = "broker.local", ClientId = "same-id", SessionExpiryIntervalSeconds = 7200 };

        a.GetHashCode().Should().NotBe(b.GetHashCode());
    }

    [Test]
    public void Clone_PreservesCleanStart()
    {
        var conn = new Connection { CleanStart = false };
        var clone = conn.Clone();

        clone.CleanStart.Should().BeFalse();
    }

    [Test]
    public void Clone_PreservesSessionExpiryIntervalSeconds()
    {
        var conn = new Connection { SessionExpiryIntervalSeconds = 7200 };
        var clone = conn.Clone();

        clone.SessionExpiryIntervalSeconds.Should().Be(7200u);
    }

    [Test]
    public void Equals_ReturnsFalseWhenReconnectDelayDiffers()
    {
        var a = new Connection { Name = "Test", Host = "broker.local", ClientId = "same-id", ReconnectDelay = 5 };
        var b = new Connection { Name = "Test", Host = "broker.local", ClientId = "same-id", ReconnectDelay = 10 };

        a.Equals(b).Should().BeFalse();
    }

    [Test]
    public void Equals_ReturnsFalseWhenKeepAlivePeriodDiffers()
    {
        var a = new Connection { Name = "Test", Host = "broker.local", ClientId = "same-id", KeepAlivePeriod = 15 };
        var b = new Connection { Name = "Test", Host = "broker.local", ClientId = "same-id", KeepAlivePeriod = 30 };

        a.Equals(b).Should().BeFalse();
    }

    [Test]
    public void CloneWithoutPassword_PreservesSessionTimingFields()
    {
        var original = new Connection
        {
            Name = "Broker",
            ClientId = "my-client",
            ConnectTimeout = 20,
            ReconnectDelay = 8,
            KeepAlivePeriod = 25,
            Password = "secret"
        };

        var clone = original.CloneWithoutPassword();

        clone.ConnectTimeout.Should().Be(20);
        clone.ReconnectDelay.Should().Be(8);
        clone.KeepAlivePeriod.Should().Be(25);
        clone.Password.Should().BeNull();
    }

    [Test]
    public void JsonDeserialize_MissingCleanStart_DefaultsToTrue()
    {
        var json = """
            {
                "name": "Legacy",
                "host": "broker.local",
                "port": 1883,
                "clientId": "legacy-client"
            }
            """;

        var conn = System.Text.Json.JsonSerializer.Deserialize<Connection>(json,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            });

        conn.Should().NotBeNull();
        conn.CleanStart.Should().BeTrue();
        conn.SessionExpiryIntervalSeconds.Should().Be(3600u);
    }

    [Test]
    public void JsonRoundTrip_PreservesCleanStartAndSessionExpiry()
    {
        var original = new Connection
        {
            Name = "Persisted",
            Host = "broker.local",
            ClientId = "persist-client",
            CleanStart = false,
            SessionExpiryIntervalSeconds = 7200
        };

        var options = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        };

        var json = System.Text.Json.JsonSerializer.Serialize(original, options);
        var deserialized = System.Text.Json.JsonSerializer.Deserialize<Connection>(json, options);

        deserialized.Should().NotBeNull();
        deserialized.CleanStart.Should().BeFalse();
        deserialized.SessionExpiryIntervalSeconds.Should().Be(7200u);
    }
}
