using System.Net;
using MQTTnet.Client;
using MQTTnet.Formatter;
using MqttProbe.Models.Chart;
using MqttProbe.Models.Configuration;
using MqttProbe.Models.Mqtt;
using MqttProbe.Models.Sparkplug;
using MqttProbe.Services.Chart;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Mqtt;
using MqttProbe.Services.Platform;
using MqttProbe.Services.Security;
using MqttProbe.Services.Sparkplug;
using MqttProbe.Services.Telemetry;

namespace MqttProbe.Shared.Tests.Services.Mqtt;

[TestFixture]
public class MqttOptionsBuilderTests
{
    private MqttOptionsBuilder _builder = null!;

    [SetUp]
    public void Setup() => _builder = new MqttOptionsBuilder();

    private static Connection TcpConnection(string host = "broker.local", int port = 1883) =>
        new() { Name = "Test", Host = host, Port = port, Protocol = Protocol.Mqtt, ClientId = "test-client" };

    private static MqttClientTcpOptions TcpOpts(MQTTnet.Extensions.ManagedClient.ManagedMqttClientOptions opts) =>
        (MqttClientTcpOptions)((MqttClientOptions)opts.ClientOptions).ChannelOptions!;

    [Test]
    public void Build_TcpConnection_UsesCorrectHostAndPort()
    {
        var options = _builder.Build(TcpConnection("mqtt.example.com", 8883));

        var tcp = TcpOpts(options);
        tcp.RemoteEndpoint.Should().NotBeNull();
        var endpoint = tcp.RemoteEndpoint.Should().BeOfType<DnsEndPoint>().Subject;
        endpoint.Host.Should().Be("mqtt.example.com");
        endpoint.Port.Should().Be(8883);
    }

    [Test]
    public void Build_TcpConnection_SetsClientId()
    {
        var conn = TcpConnection();
        conn.ClientId = "my-client-id";
        var options = _builder.Build(conn);
        ((MqttClientOptions)options.ClientOptions).ClientId.Should().StartWith("my-client-id-");
    }

    [Test]
    public void Build_TwoInstances_ProduceDifferentClientIds()
    {
        var conn = TcpConnection();
        conn.ClientId = "shared-id";
        var id1 = ((MqttClientOptions)_builder.Build(conn).ClientOptions).ClientId;
        var id2 = ((MqttClientOptions)new MqttOptionsBuilder().Build(conn).ClientOptions).ClientId;
        id1.Should().NotBe(id2);
    }

    [Test]
    public void Build_SameInstance_ProducesSameClientId()
    {
        var conn = TcpConnection();
        conn.ClientId = "shared-id";
        var id1 = ((MqttClientOptions)_builder.Build(conn).ClientOptions).ClientId;
        var id2 = ((MqttClientOptions)_builder.Build(conn).ClientOptions).ClientId;
        id1.Should().Be(id2);
    }

    [Test]
    public void Build_TcpConnection_SetsCredentials_WhenUserProvided()
    {
        var conn = TcpConnection();
        conn.User = "admin";
        conn.Password = "secret";
        var options = _builder.Build(conn);
        ((MqttClientOptions)options.ClientOptions).Credentials.Should().NotBeNull();
    }

    [Test]
    public void Build_TcpConnection_NoCredentials_WhenUserEmpty()
    {
        var conn = TcpConnection();
        conn.User = null;
        conn.Password = null;
        var options = _builder.Build(conn);
        ((MqttClientOptions)options.ClientOptions).Credentials.Should().BeNull();
    }

    [Test]
    public void Build_TlsEnabled_SetsTlsOptions()
    {
        var conn = TcpConnection();
        conn.UseTls = true;
        var options = _builder.Build(conn);
        TcpOpts(options).TlsOptions.UseTls.Should().BeTrue();
    }

    [Test]
    public void Build_AllowUntrustedCert_SetsValidationHandler()
    {
        var conn = TcpConnection();
        conn.UseTls = true;
        conn.AllowUntrustedCertificate = true;
        var options = _builder.Build(conn);
        var tls = TcpOpts(options).TlsOptions;
        tls.AllowUntrustedCertificates.Should().BeTrue();
        tls.CertificateValidationHandler.Should().NotBeNull();
    }

    [Test]
    public void Build_WebSocketProtocol_UsesWsScheme()
    {
        var conn = new Connection
        {
            Name = "WS",
            Host = "broker.local",
            Port = 8080,
            Protocol = Protocol.WebSocket,
            ClientId = "ws-client",
            WebsocketBasePath = "mqtt",
            UseTls = false
        };
        var options = _builder.Build(conn);
        var ws = (MqttClientWebSocketOptions)((MqttClientOptions)options.ClientOptions).ChannelOptions!;
        ws.Uri.Should().StartWith("ws://");
    }

    [Test]
    public void Build_WebSocketWithTls_UsesWssScheme()
    {
        var conn = new Connection
        {
            Name = "WSS",
            Host = "broker.local",
            Port = 443,
            Protocol = Protocol.WebSocket,
            ClientId = "wss-client",
            WebsocketBasePath = "mqtt",
            UseTls = true
        };
        var options = _builder.Build(conn);
        var ws = (MqttClientWebSocketOptions)((MqttClientOptions)options.ClientOptions).ChannelOptions!;
        ws.Uri.Should().StartWith("wss://");
    }

    [Test]
    public void Build_SetsAutoReconnectDelay()
    {
        var options = _builder.Build(TcpConnection());
        options.AutoReconnectDelay.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Test]
    public void Build_NullConnection_ThrowsArgumentNullException()
    {
        var act = () => _builder.Build(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void Build_MqttVersion311_SetsV311ProtocolVersion()
    {
        var conn = TcpConnection();
        conn.MqttVersion = MqttVersion.V311;
        var options = _builder.Build(conn);
        ((MqttClientOptions)options.ClientOptions).ProtocolVersion.Should().Be(MqttProtocolVersion.V311);
    }

    [Test]
    public void Build_MqttVersion5_SetsV500ProtocolVersion()
    {
        var conn = TcpConnection();
        conn.MqttVersion = MqttVersion.V5;
        var options = _builder.Build(conn);
        ((MqttClientOptions)options.ClientOptions).ProtocolVersion.Should().Be(MqttProtocolVersion.V500);
    }
}
