using System.Security.Cryptography.X509Certificates;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;
using MQTTnet.Formatter;
using MqttProbe.Models.Mqtt;
using MqttProbe.Services.Security;

namespace MqttProbe.Services.Mqtt;

public interface IMqttOptionsBuilder
{
    public ManagedMqttClientOptions Build(Connection connection);
    public Task<ManagedMqttClientOptions> BuildAsync(Connection connection, CertificateSessionResource certResource);
}

public class MqttOptionsBuilder : IMqttOptionsBuilder
{
    private readonly ICertificateAssetStore _certStore;
    private readonly string _sessionSuffix = "-" + Guid.NewGuid().ToString("N")[..6];

    public MqttOptionsBuilder(ICertificateAssetStore certStore)
    {
        _certStore = certStore;
    }

    public ManagedMqttClientOptions Build(Connection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var clientOptionsBuilder = new MqttClientOptionsBuilder()
            .WithClientId(connection.ClientId + _sessionSuffix)
            .WithTimeout(TimeSpan.FromSeconds(connection.ConnectTimeout > 0 ? connection.ConnectTimeout : 15))
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(
                connection.KeepAlivePeriod > 0 ? connection.KeepAlivePeriod : 15))
            .WithProtocolVersion(connection.MqttVersion == MqttVersion.V5
                ? MqttProtocolVersion.V500
                : MqttProtocolVersion.V311);

        if (!string.IsNullOrEmpty(connection.User))
            clientOptionsBuilder.WithCredentials(connection.User, connection.Password);

        if (connection.Protocol == Protocol.Mqtt)
        {
            clientOptionsBuilder.WithTcpServer(connection.Host, connection.Port);
        }
        else
        {
            var wsScheme = connection.UseTls ? "wss" : "ws";
            var path = (connection.WebsocketBasePath ?? string.Empty).Trim().TrimStart('/');
            clientOptionsBuilder.WithWebSocketServer(opt =>
            {
                opt.WithUri(string.IsNullOrEmpty(path)
                    ? $"{wsScheme}://{connection.Host}:{connection.Port}/"
                    : $"{wsScheme}://{connection.Host}:{connection.Port}/{path}");
            });
        }

        if (connection.UseTls)
        {
            var tlsBuilder = new MqttClientTlsOptionsBuilder()
                .WithSslProtocols(System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13);
            if (connection.AllowUntrustedCertificate)
                tlsBuilder = tlsBuilder.WithAllowUntrustedCertificates().WithCertificateValidationHandler(_ => true);
            clientOptionsBuilder.WithTlsOptions(tlsBuilder.Build());
        }

        return new ManagedMqttClientOptionsBuilder()
            .WithAutoReconnectDelay(TimeSpan.FromSeconds(
                connection.ReconnectDelay > 0 ? connection.ReconnectDelay : 5))
            .WithClientOptions(clientOptionsBuilder)
            .Build();
    }

    public async Task<ManagedMqttClientOptions> BuildAsync(
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

        var clientOptionsBuilder = new MqttClientOptionsBuilder()
            .WithClientId(connection.ClientId + _sessionSuffix)
            .WithTimeout(TimeSpan.FromSeconds(connection.ConnectTimeout > 0 ? connection.ConnectTimeout : 15))
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(
                connection.KeepAlivePeriod > 0 ? connection.KeepAlivePeriod : 15))
            .WithProtocolVersion(connection.MqttVersion == MqttVersion.V5
                ? MqttProtocolVersion.V500
                : MqttProtocolVersion.V311);

        if (!string.IsNullOrEmpty(connection.User))
            clientOptionsBuilder.WithCredentials(connection.User, connection.Password);

        if (connection.Protocol == Protocol.Mqtt)
        {
            clientOptionsBuilder.WithTcpServer(connection.Host, connection.Port);
        }
        else
        {
            var wsScheme = connection.UseTls ? "wss" : "ws";
            var path = (connection.WebsocketBasePath ?? string.Empty).Trim().TrimStart('/');
            clientOptionsBuilder.WithWebSocketServer(opt =>
            {
                opt.WithUri(string.IsNullOrEmpty(path)
                    ? $"{wsScheme}://{connection.Host}:{connection.Port}/"
                    : $"{wsScheme}://{connection.Host}:{connection.Port}/{path}");
            });
        }

        if (connection.UseTls)
        {
            var tlsBuilder = new MqttClientTlsOptionsBuilder()
                .WithSslProtocols(System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13);
            if (connection.AllowUntrustedCertificate)
                tlsBuilder = tlsBuilder.WithAllowUntrustedCertificates().WithCertificateValidationHandler(_ => true);
            if (clientCerts is not null)
                tlsBuilder = tlsBuilder.WithClientCertificates(clientCerts);
            clientOptionsBuilder.WithTlsOptions(tlsBuilder.Build());
        }

        return new ManagedMqttClientOptionsBuilder()
            .WithAutoReconnectDelay(TimeSpan.FromSeconds(
                connection.ReconnectDelay > 0 ? connection.ReconnectDelay : 5))
            .WithClientOptions(clientOptionsBuilder)
            .Build();
    }
}
