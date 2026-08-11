using System.Net;
using MQTTnet;
using MQTTnet.Formatter;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Core.Services.Security;
using MqttProbe.TestInfrastructure.Security;

namespace MqttProbe.Core.Tests.Services.Mqtt;

[TestFixture]
public class MqttOptionsBuilderTests
{
    private ICertificateAssetStore _mockCertStore = null!;
    private MqttOptionsBuilder _builder = null!;

    [SetUp]
    public void Setup()
    {
        _mockCertStore = Substitute.For<ICertificateAssetStore>();
        _builder = new MqttOptionsBuilder(_mockCertStore);
    }

    private static Connection TcpConnection(string host = "broker.local", int port = 1883) =>
        new() { Name = "Test", Host = host, Port = port, Protocol = Protocol.Mqtt, ClientId = "test-client" };

    private static MqttClientTcpOptions TcpOpts(MqttManagedClientOptions opts) =>
        (MqttClientTcpOptions)opts.ClientOptions.ChannelOptions!;

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
        options.ClientOptions.ClientId.Should().StartWith("my-client-id-");
    }

    [Test]
    public void Build_TwoInstances_ProduceDifferentClientIds()
    {
        var conn = TcpConnection();
        conn.ClientId = "shared-id";
        var id1 = _builder.Build(conn).ClientOptions.ClientId;
        var id2 = new MqttOptionsBuilder(_mockCertStore).Build(conn).ClientOptions.ClientId;
        id1.Should().NotBe(id2);
    }

    [Test]
    public void Build_SameInstance_ProducesSameClientId()
    {
        var conn = TcpConnection();
        conn.ClientId = "shared-id";
        var id1 = _builder.Build(conn).ClientOptions.ClientId;
        var id2 = _builder.Build(conn).ClientOptions.ClientId;
        id1.Should().Be(id2);
    }

    [Test]
    public void Build_TcpConnection_SetsCredentials_WhenUserProvided()
    {
        var conn = TcpConnection();
        conn.User = "admin";
        conn.Password = "secret";
        var options = _builder.Build(conn);
        options.ClientOptions.Credentials.Should().NotBeNull();
    }

    [Test]
    public void Build_TcpConnection_NoCredentials_WhenUserEmpty()
    {
        var conn = TcpConnection();
        conn.User = null;
        conn.Password = null;
        var options = _builder.Build(conn);
        options.ClientOptions.Credentials.Should().BeNull();
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
        var ws = (MqttClientWebSocketOptions)options.ClientOptions.ChannelOptions!;
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
        var ws = (MqttClientWebSocketOptions)options.ClientOptions.ChannelOptions!;
        ws.Uri.Should().StartWith("wss://");
    }

    [Test]
    public void Build_WebSocketPathMqtt_ProducesCorrectUri()
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
        var ws = (MqttClientWebSocketOptions)options.ClientOptions.ChannelOptions!;
        ws.Uri.Should().Be("ws://broker.local:8080/mqtt");
    }

    [Test]
    public void Build_WebSocketPathWithLeadingSlash_NoDoubleSlash()
    {
        var conn = new Connection
        {
            Name = "WS",
            Host = "broker.local",
            Port = 8080,
            Protocol = Protocol.WebSocket,
            ClientId = "ws-client",
            WebsocketBasePath = "/mqtt",
            UseTls = false
        };
        var options = _builder.Build(conn);
        var ws = (MqttClientWebSocketOptions)options.ClientOptions.ChannelOptions!;
        ws.Uri.Should().Be("ws://broker.local:8080/mqtt");
    }

    [Test]
    public void Build_WebSocketPathEmpty_NoDoubleSlash()
    {
        var conn = new Connection
        {
            Name = "WS",
            Host = "broker.local",
            Port = 8080,
            Protocol = Protocol.WebSocket,
            ClientId = "ws-client",
            WebsocketBasePath = "",
            UseTls = false
        };
        var options = _builder.Build(conn);
        var ws = (MqttClientWebSocketOptions)options.ClientOptions.ChannelOptions!;
        ws.Uri.Should().Be("ws://broker.local:8080/");
    }

    [Test]
    public void Build_WebSocketPathWhitespace_NoDoubleSlash()
    {
        var conn = new Connection
        {
            Name = "WS",
            Host = "broker.local",
            Port = 8080,
            Protocol = Protocol.WebSocket,
            ClientId = "ws-client",
            WebsocketBasePath = "   ",
            UseTls = false
        };
        var options = _builder.Build(conn);
        var ws = (MqttClientWebSocketOptions)options.ClientOptions.ChannelOptions!;
        ws.Uri.Should().Be("ws://broker.local:8080/");
    }

    [Test]
    public void Build_DefaultAutoReconnectDelay_Uses5Seconds()
    {
        var options = _builder.Build(TcpConnection());
        options.AutoReconnectDelay.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Test]
    public void Build_CustomReconnectDelay_UsesConfiguredValue()
    {
        var conn = TcpConnection();
        conn.ReconnectDelay = 12;
        var options = _builder.Build(conn);
        options.AutoReconnectDelay.Should().Be(TimeSpan.FromSeconds(12));
    }

    [Test]
    public void Build_ZeroReconnectDelay_FallsBackTo5Seconds()
    {
        var conn = TcpConnection();
        conn.ReconnectDelay = 0;
        var options = _builder.Build(conn);
        options.AutoReconnectDelay.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Test]
    public void Build_DefaultKeepAlivePeriod_Uses15Seconds()
    {
        var options = _builder.Build(TcpConnection());
        options.ClientOptions.KeepAlivePeriod.Should().Be(TimeSpan.FromSeconds(15));
    }

    [Test]
    public void Build_CustomKeepAlivePeriod_UsesConfiguredValue()
    {
        var conn = TcpConnection();
        conn.KeepAlivePeriod = 30;
        var options = _builder.Build(conn);
        options.ClientOptions.KeepAlivePeriod.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Test]
    public void Build_ZeroKeepAlivePeriod_FallsBackTo15Seconds()
    {
        var conn = TcpConnection();
        conn.KeepAlivePeriod = 0;
        var options = _builder.Build(conn);
        options.ClientOptions.KeepAlivePeriod.Should().Be(TimeSpan.FromSeconds(15));
    }

    [Test]
    public void Build_DefaultConnectTimeout_Uses15Seconds()
    {
        var options = _builder.Build(TcpConnection());
        options.ClientOptions.Timeout.Should().Be(TimeSpan.FromSeconds(15));
    }

    [Test]
    public void Build_CustomConnectTimeout_UsesConfiguredValue()
    {
        var conn = TcpConnection();
        conn.ConnectTimeout = 30;
        var options = _builder.Build(conn);
        options.ClientOptions.Timeout.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Test]
    public void Build_ZeroConnectTimeout_FallsBackTo15Seconds()
    {
        var conn = TcpConnection();
        conn.ConnectTimeout = 0;
        var options = _builder.Build(conn);
        options.ClientOptions.Timeout.Should().Be(TimeSpan.FromSeconds(15));
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
        options.ClientOptions.ProtocolVersion.Should().Be(MqttProtocolVersion.V311);
    }

    [Test]
    public void Build_MqttVersion5_SetsV500ProtocolVersion()
    {
        var conn = TcpConnection();
        conn.MqttVersion = MqttVersion.V5;
        var options = _builder.Build(conn);
        options.ClientOptions.ProtocolVersion.Should().Be(MqttProtocolVersion.V500);
    }

    [Test]
    public async Task BuildAsync_WithCertAsset_LoadsAndSetsCertificate()
    {
        var conn = TcpConnection();
        conn.UseTls = true;
        conn.ClientCertificateAssetId = "test-asset";
        using var cert = TestCertFactory.CreateRsaCert();
        _mockCertStore.LoadAsync(conn.Id, "test-asset")
            .Returns(new ClientCertificateBundle(cert));

        var resource = new CertificateSessionResource();
        await _builder.BuildAsync(conn, resource);

        resource.Certificate.Should().NotBeNull();
        resource.Certificate!.HasPrivateKey.Should().BeTrue();
    }

    [Test]
    public async Task BuildAsync_WithCertAsset_CertReachesTlsOptions()
    {
        var conn = TcpConnection();
        conn.UseTls = true;
        conn.ClientCertificateAssetId = "test-asset";
        using var cert = TestCertFactory.CreateRsaCert();
        _mockCertStore.LoadAsync(conn.Id, "test-asset")
            .Returns(new ClientCertificateBundle(cert));

        var resource = new CertificateSessionResource();
        var options = await _builder.BuildAsync(conn, resource);

        var tcp = TcpOpts(options);
        var clientCerts = tcp.TlsOptions.ClientCertificatesProvider?.GetCertificates();
        clientCerts.Should().NotBeNull();
        clientCerts.Count.Should().BeGreaterThan(0);
    }

    [Test]
    public async Task BuildAsync_MissingAsset_ThrowsCertificateAssetUnavailableException()
    {
        var conn = TcpConnection();
        conn.UseTls = true;
        conn.ClientCertificateAssetId = "missing";
        _mockCertStore.LoadAsync(conn.Id, "missing").Returns((ClientCertificateBundle?)null);

        var resource = new CertificateSessionResource();
        var act = () => _builder.BuildAsync(conn, resource);
        await act.Should().ThrowAsync<CertificateAssetUnavailableException>();
    }

    [Test]
    public async Task BuildAsync_NoCertAsset_ResourceCertificateRemainsNull()
    {
        var conn = TcpConnection();
        conn.UseTls = true;
        var resource = new CertificateSessionResource();
        await _builder.BuildAsync(conn, resource);
        resource.Certificate.Should().BeNull();
    }

    [Test]
    public void Build_CleanStartTrue_SetsCleanSessionTrue()
    {
        var conn = TcpConnection();
        conn.CleanStart = true;
        var options = _builder.Build(conn);

        options.ClientOptions.CleanSession.Should().BeTrue();
    }

    [Test]
    public void Build_CleanStartFalse_SetsCleanSessionFalse()
    {
        var conn = TcpConnection();
        conn.CleanStart = false;
        var options = _builder.Build(conn);

        options.ClientOptions.CleanSession.Should().BeFalse();
    }

    [Test]
    public void Build_CleanStartTrue_ClientIdHasSuffix()
    {
        var conn = TcpConnection();
        conn.ClientId = "test-client";
        conn.CleanStart = true;
        var options = _builder.Build(conn);

        options.ClientOptions.ClientId.Should().StartWith("test-client-");
    }

    [Test]
    public void Build_CleanStartFalse_ClientIdHasNoSuffix()
    {
        var conn = TcpConnection();
        conn.ClientId = "test-client";
        conn.CleanStart = false;
        var options = _builder.Build(conn);

        options.ClientOptions.ClientId.Should().Be("test-client");
    }

    [Test]
    public void Build_V5CleanStartFalse_SetsSessionExpiryInterval()
    {
        var conn = TcpConnection();
        conn.MqttVersion = MqttVersion.V5;
        conn.CleanStart = false;
        conn.SessionExpiryIntervalSeconds = 7200;
        var options = _builder.Build(conn);

        options.ClientOptions.SessionExpiryInterval.Should().Be(7200u);
    }

    [Test]
    public void Build_V5CleanStartTrue_DoesNotSetSessionExpiryInterval()
    {
        var conn = TcpConnection();
        conn.MqttVersion = MqttVersion.V5;
        conn.CleanStart = true;
        conn.SessionExpiryIntervalSeconds = 7200;
        var options = _builder.Build(conn);

        options.ClientOptions.SessionExpiryInterval.Should().Be(0u);
    }

    [Test]
    public void Build_V311CleanStartFalse_CleanSessionFalseNoSessionExpiry()
    {
        var conn = TcpConnection();
        conn.MqttVersion = MqttVersion.V311;
        conn.CleanStart = false;
        var options = _builder.Build(conn);

        options.ClientOptions.CleanSession.Should().BeFalse();
        options.ClientOptions.SessionExpiryInterval.Should().Be(0u);
    }

    [Test]
    public void Build_V5CleanStartTrue_UsesWithCleanStart()
    {
        var conn = TcpConnection();
        conn.MqttVersion = MqttVersion.V5;
        conn.CleanStart = true;
        var options = _builder.Build(conn);

        options.ClientOptions.CleanSession.Should().BeTrue();
    }

    [Test]
    public void Build_DefaultCleanStart_ClientIdHasSuffix()
    {
        var conn = TcpConnection();
        conn.ClientId = "test-client";
        // CleanStart defaults to true
        var options = _builder.Build(conn);

        options.ClientOptions.ClientId.Should().StartWith("test-client-");
    }
}
