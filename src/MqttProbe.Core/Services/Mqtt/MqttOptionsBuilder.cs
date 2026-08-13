using System.Security.Cryptography.X509Certificates;
using MQTTnet;
using MQTTnet.Formatter;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Security;

namespace MqttProbe.Core.Services.Mqtt;

public interface IMqttOptionsBuilder
{
    public MqttManagedClientOptions Build(Connection connection);
    public Task<MqttManagedClientOptions> BuildAsync(Connection connection, CertificateSessionResource certResource);
}

public class MqttOptionsBuilder : IMqttOptionsBuilder
{
    private readonly ICertificateAssetStore _certStore;
    private readonly string _sessionSuffix = "-" + Guid.NewGuid().ToString("N")[..6];

    public MqttOptionsBuilder(ICertificateAssetStore certStore)
    {
        _certStore = certStore;
    }

    public MqttManagedClientOptions Build(Connection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var builder = CreateClientOptionsBuilder(connection);

        ConfigureTransport(builder, connection);
        ConfigureTls(builder, connection);

        return new MqttManagedClientOptions
        {
            ClientOptions = builder.Build(),
            AutoReconnectDelay = TimeSpan.FromSeconds(
                connection.ReconnectDelay > 0 ? connection.ReconnectDelay : 5)
        };
    }

    public async Task<MqttManagedClientOptions> BuildAsync(
        Connection connection, CertificateSessionResource certResource)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(certResource);

        X509Certificate2Collection? clientCerts = null;
        if (connection.UseTls && connection.ClientCertificateAssetId is not null)
        {
            var bundle = await _certStore.LoadAsync(connection.Id, connection.ClientCertificateAssetId);
            if (bundle is null)
                throw new CertificateAssetUnavailableException(connection.ClientCertificateAssetId);
            certResource.Set(bundle.Certificate);
            clientCerts = new X509Certificate2Collection(certResource.Certificate!);
        }

        var builder = CreateClientOptionsBuilder(connection);

        ConfigureTransport(builder, connection);
        ConfigureTls(builder, connection, clientCerts);

        return new MqttManagedClientOptions
        {
            ClientOptions = builder.Build(),
            AutoReconnectDelay = TimeSpan.FromSeconds(
                connection.ReconnectDelay > 0 ? connection.ReconnectDelay : 5)
        };
    }

    private MqttClientOptionsBuilder CreateClientOptionsBuilder(Connection connection)
    {
        var clientId = connection.CleanStart
            ? connection.ClientId + _sessionSuffix
            : connection.ClientId;

        var builder = new MqttClientOptionsBuilder()
            .WithClientId(clientId)
            .WithTimeout(TimeSpan.FromSeconds(connection.ConnectTimeout > 0 ? connection.ConnectTimeout : 15))
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(
                connection.KeepAlivePeriod > 0 ? connection.KeepAlivePeriod : 15))
            .WithProtocolVersion(connection.MqttVersion == MqttVersion.V5
                ? MqttProtocolVersion.V500
                : MqttProtocolVersion.V311);

        if (connection.MqttVersion == MqttVersion.V5)
            builder.WithCleanStart(connection.CleanStart);
        else
            builder.WithCleanSession(connection.CleanStart);

        if (connection.MqttVersion == MqttVersion.V5 && !connection.CleanStart)
            builder.WithSessionExpiryInterval(connection.SessionExpiryIntervalSeconds);

        if (!string.IsNullOrEmpty(connection.User))
            builder.WithCredentials(connection.User, connection.Password);

        return builder;
    }

    private static void ConfigureTransport(MqttClientOptionsBuilder builder, Connection connection)
    {
        if (connection.Protocol == Protocol.Mqtt)
        {
            builder.WithTcpServer(connection.Host, connection.Port);
        }
        else
        {
            var wsScheme = connection.UseTls ? "wss" : "ws";
            var path = (connection.WebsocketBasePath).Trim().TrimStart('/');
            builder.WithWebSocketServer(opt =>
            {
                opt.WithUri(string.IsNullOrEmpty(path)
                    ? $"{wsScheme}://{connection.Host}:{connection.Port}/"
                    : $"{wsScheme}://{connection.Host}:{connection.Port}/{path}");
            });
        }
    }

    private static void ConfigureTls(
        MqttClientOptionsBuilder builder, Connection connection,
        X509Certificate2Collection? clientCerts = null)
    {
        if (!connection.UseTls) return;

        // Pinned, not SslProtocols.None: None defers to OS policy, which on older Windows still allows pre-1.2.
        var tlsBuilder = new MqttClientTlsOptionsBuilder()
            .WithSslProtocols(System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13); // DevSkim: ignore DS440020,DS112836,DS440001
        if (connection.AllowUntrustedCertificate)
            tlsBuilder = tlsBuilder.WithAllowUntrustedCertificates().WithCertificateValidationHandler(_ => true);
        if (clientCerts is not null)
            tlsBuilder = tlsBuilder.WithClientCertificates(clientCerts);
        builder.WithTlsOptions(tlsBuilder.Build());
    }
}
