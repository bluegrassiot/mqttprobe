using System.Security.Authentication;
using MqttProbe.Models.Emulation;
using MqttProbe.Models.Mqtt;
using MqttProbe.Services.Emulation;
using MqttProbe.Services.Security;
using MqttProbe.Tests.Services.Security.TestHelpers;

namespace MqttProbe.Shared.Tests.Services.Emulation;

[TestFixture]
public class SparkplugNodeOptionsBuilderTests
{
    private static Connection TcpConnection() => new()
    {
        Name = "Test",
        Host = "broker.local",
        Port = 1883,
        Protocol = Protocol.Mqtt,
        ClientId = "primary-client",
        User = "u",
        Password = "p",
        ConnectTimeout = 15,
        ReconnectDelay = 5,
        KeepAlivePeriod = 15
    };

    private static EmulatorNodeConfig Node(string nodeId = "Press-01") => new()
    {
        NodeId = nodeId,
        GroupId = "Plant"
    };

    [Test]
    public void Build_ClientId_IsNodeIdPlusSixHexSuffix()
    {
        var options = SparkplugNodeOptionsBuilder.Build(TcpConnection(), Node());

        options.ClientId.Should().StartWith("Press-01-");
        options.ClientId.Length.Should().Be("Press-01-".Length + 6);
        options.ClientId["Press-01-".Length..].Should().MatchRegex("^[0-9a-f]{6}$");
    }

    [Test]
    public void Build_EdgeNodeIdentifier_RemainsBareNodeId()
    {
        var options = SparkplugNodeOptionsBuilder.Build(TcpConnection(), Node());

        options.EdgeNodeIdentifier.Should().Be("Press-01");
        options.GroupIdentifier.Should().Be("Plant");
    }

    [Test]
    public void Build_TwoCalls_ProduceDifferentClientIds()
    {
        var conn = TcpConnection();
        var node = Node();

        var id1 = SparkplugNodeOptionsBuilder.Build(conn, node).ClientId;
        var id2 = SparkplugNodeOptionsBuilder.Build(conn, node).ClientId;

        id1.Should().NotBe(id2);
    }

    [Test]
    public void Build_DefaultReconnectDelay_Uses5Seconds()
    {
        var options = SparkplugNodeOptionsBuilder.Build(TcpConnection(), Node());

        options.ReconnectInterval.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Test]
    public void Build_CustomReconnectDelay_UsesConfiguredValue()
    {
        var conn = TcpConnection();
        conn.ReconnectDelay = 12;

        var options = SparkplugNodeOptionsBuilder.Build(conn, Node());

        options.ReconnectInterval.Should().Be(TimeSpan.FromSeconds(12));
    }

    [Test]
    public void Build_ZeroReconnectDelay_FallsBackTo5Seconds()
    {
        var conn = TcpConnection();
        conn.ReconnectDelay = 0;

        var options = SparkplugNodeOptionsBuilder.Build(conn, Node());

        options.ReconnectInterval.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Test]
    public void Build_WebSocket_UsesWsSchemeAndBasePath()
    {
        var conn = TcpConnection();
        conn.Protocol = Protocol.WebSocket;
        conn.WebsocketBasePath = "mqtt";

        var options = SparkplugNodeOptionsBuilder.Build(conn, Node());

        options.BrokerAddress.Should().Be("ws://broker.local:1883/mqtt");
        options.MqttWebSocketOptions.Should().NotBeNull();
        options.MqttWebSocketOptions!.Uri.Should().Be("ws://broker.local:1883/mqtt");
    }

    [Test]
    public void Build_WebSocketOverTls_UsesWssSchemeAndTrimsBasePath()
    {
        var conn = TcpConnection();
        conn.Protocol = Protocol.WebSocket;
        conn.UseTls = true;
        conn.WebsocketBasePath = "  /custom/path  ";

        var options = SparkplugNodeOptionsBuilder.Build(conn, Node());

        options.BrokerAddress.Should().Be("wss://broker.local:1883/custom/path");
    }

    [Test]
    public void Build_WebSocketWithoutBasePath_UsesRootPath()
    {
        var conn = TcpConnection();
        conn.Protocol = Protocol.WebSocket;
        conn.WebsocketBasePath = string.Empty;

        var options = SparkplugNodeOptionsBuilder.Build(conn, Node());

        options.BrokerAddress.Should().Be("ws://broker.local:1883/");
    }

    [Test]
    public void Build_TlsEnabled_PinsTls12AndTls13()
    {
        var conn = TcpConnection();
        conn.UseTls = true;

        var options = SparkplugNodeOptionsBuilder.Build(conn, Node());

        options.MqttTlsOptions.Should().NotBeNull();
        options.MqttTlsOptions!.SslProtocol.Should().Be(
            SslProtocols.Tls12 | SslProtocols.Tls13); // DevSkim: ignore DS440020,DS112836,DS440001
    }

    [Test]
    public void Build_AllowUntrustedCertificate_SetsValidationHandler()
    {
        var conn = TcpConnection();
        conn.UseTls = true;
        conn.AllowUntrustedCertificate = true;

        var options = SparkplugNodeOptionsBuilder.Build(conn, Node());

        options.MqttTlsOptions.Should().NotBeNull();
        options.MqttTlsOptions!.AllowUntrustedCertificates.Should().BeTrue();
        options.MqttTlsOptions.CertificateValidationHandler.Should().NotBeNull();
    }

    [Test]
    public void Build_WithClientCertificate_AddsItToTlsOptions()
    {
        var conn = TcpConnection();
        conn.UseTls = true;
        using var resource = new CertificateSessionResource();
        resource.Set(TestCertFactory.CreateRsaCert());

        var options = SparkplugNodeOptionsBuilder.Build(conn, Node(), resource);

        var clientCerts = options.MqttTlsOptions?.ClientCertificatesProvider?.GetCertificates();
        clientCerts.Should().NotBeNull();
        clientCerts.Count.Should().Be(1);
    }

    [Test]
    public void Build_TlsDisabled_LeavesClientCertificatesUnset()
    {
        using var resource = new CertificateSessionResource();
        resource.Set(TestCertFactory.CreateRsaCert());

        var options = SparkplugNodeOptionsBuilder.Build(TcpConnection(), Node(), resource);

        options.MqttTlsOptions?.ClientCertificatesProvider.Should().BeNull();
    }
}
