using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;
using MQTTnet.Formatter;
using MqttProbe.Models.Mqtt;

namespace MqttProbe.Services.Mqtt;

public interface IMqttOptionsBuilder
{
    public ManagedMqttClientOptions Build(Connection connection);
}

public class MqttOptionsBuilder : IMqttOptionsBuilder
{
    private readonly string _sessionSuffix = "-" + Guid.NewGuid().ToString("N")[..6];

    public ManagedMqttClientOptions Build(Connection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var clientOptionsBuilder = new MqttClientOptionsBuilder()
            .WithClientId(connection.ClientId + _sessionSuffix)
            .WithTimeout(TimeSpan.FromSeconds(connection.ConnectTimeout > 0 ? connection.ConnectTimeout : 15))
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
            .WithAutoReconnectDelay(TimeSpan.FromSeconds(5))
            .WithClientOptions(clientOptionsBuilder)
            .Build();
    }
}
