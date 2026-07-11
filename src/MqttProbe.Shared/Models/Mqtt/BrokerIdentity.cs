namespace MqttProbe.Models.Mqtt;

public sealed record BrokerIdentity(
    string? Host,
    int Port,
    Protocol Protocol,
    bool UseTls,
    string? WebsocketBasePath)
{
    public static BrokerIdentity FromConnection(Connection connection) =>
        new(
            NormalizeHost(connection.Host),
            connection.Port,
            connection.Protocol,
            connection.UseTls,
            NormalizeWebsocketPath(connection.WebsocketBasePath));

    private static string? NormalizeHost(string? host) =>
        string.IsNullOrWhiteSpace(host) ? host : host.Trim().ToLowerInvariant();

    private static string? NormalizeWebsocketPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        path = path.Trim();
        return path.StartsWith('/') ? path : "/" + path;
    }
}
