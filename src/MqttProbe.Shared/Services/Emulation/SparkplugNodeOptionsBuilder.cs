using System.Security.Cryptography.X509Certificates;
using MQTTnet;
using MqttProbe.Models.Emulation;
using MqttProbe.Models.Mqtt;
using MqttProbe.Services.Security;
using SparkplugNet.Core.Enumerations;
using SparkplugNet.Core.Node;

namespace MqttProbe.Services.Emulation;

internal static class SparkplugNodeOptionsBuilder
{
    public static SparkplugNodeOptions Build(
        Connection connection, EmulatorNodeConfig config,
        CertificateSessionResource? certResource = null,
        string? mqttClientId = null)
    {
        MqttClientWebSocketOptions? webSocketOptions = null;
        var brokerAddress = connection.Host;
        if (connection.Protocol == Protocol.WebSocket)
        {
            var scheme = connection.UseTls ? "wss" : "ws";
            var wsPath = (connection.WebsocketBasePath).Trim().TrimStart('/');
            brokerAddress = string.IsNullOrEmpty(wsPath)
                ? $"{scheme}://{connection.Host}:{connection.Port}/"
                : $"{scheme}://{connection.Host}:{connection.Port}/{wsPath}";
            webSocketOptions = new MqttClientWebSocketOptions { Uri = brokerAddress };
        }

        var tlsOptions = new MqttClientTlsOptions();
        if (connection.UseTls)
        {
            // Pinned, not SslProtocols.None: None defers to OS policy, which on older Windows still allows pre-1.2.
            var tlsBuilder = new MqttClientTlsOptionsBuilder()
                .WithSslProtocols(System.Security.Authentication.SslProtocols.Tls12 | // DevSkim: ignore DS440020,DS112836,DS440001
                                  System.Security.Authentication.SslProtocols.Tls13); // DevSkim: ignore DS440020,DS112836,DS440001
            if (connection.AllowUntrustedCertificate)
                tlsBuilder = tlsBuilder.WithAllowUntrustedCertificates()
                                       .WithCertificateValidationHandler(_ => true);
            if (certResource?.Certificate is not null)
                tlsBuilder = tlsBuilder.WithClientCertificates(
                    new X509Certificate2Collection(certResource.Certificate));
            tlsOptions = tlsBuilder.Build();
        }

        mqttClientId ??= config.NodeId + "-" + Guid.NewGuid().ToString("N")[..6];
        var reconnectSeconds = connection.ReconnectDelay > 0 ? connection.ReconnectDelay : 5;

        return new SparkplugNodeOptions(
            brokerAddress,
            connection.Port,
            mqttClientId,
            connection.User,
            connection.Password,
            null,
            TimeSpan.FromSeconds(reconnectSeconds),
            SparkplugMqttProtocolVersion.V311,
            tlsOptions,
            webSocketOptions,
            config.GroupId,
            config.NodeId,
            CancellationToken.None);
    }
}
