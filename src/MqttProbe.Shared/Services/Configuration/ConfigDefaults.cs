using MqttProbe.Models.Configuration;
using MqttProbe.Models.Mqtt;

namespace MqttProbe.Services.Configuration;

internal static class ConfigDefaults
{
    // Guards against explicit nulls in the config file; the model's own initializers only
    // cover properties the JSON omits entirely.
    public static void Normalize(AppConfiguration config)
    {
        config.Connections ??= [];
        config.Auth ??= new Auth();
        config.Performance ??= new PerformanceSettings();
        config.Ui ??= new UiPreferences();
        config.Ui.DismissedHints ??= [];
        config.ChartsByConnection ??= [];
        config.EmulatorsByConnection ??= [];
    }

    // Seeded on first run so a user without their own broker can try the app immediately.
    // ClientId is left to auto-generate per install to avoid collisions on these shared public brokers.
    public static List<Connection> SeedConnections() =>
    [
        new()
        {
            Name = "HiveMQ — TCP 1883",
            Host = "broker.hivemq.com",
            Port = 1883,
            Protocol = Protocol.Mqtt,
            UseTls = false,
            SubscribedTopics = [new SubscribedTopic { Topic = "spBv1.0/#" }]
        },
        new()
        {
            Name = "EMQX — TLS 8883",
            Host = "broker.emqx.io",
            Port = 8883,
            Protocol = Protocol.Mqtt,
            UseTls = true,
            AllowUntrustedCertificate = false,
            SubscribedTopics = [new SubscribedTopic { Topic = "spBv1.0/#" }]
        },
        new()
        {
            Name = "Mosquitto — WebSocket TLS 8081",
            Host = "test.mosquitto.org",
            Port = 8081,
            Protocol = Protocol.WebSocket,
            UseTls = true,
            AllowUntrustedCertificate = false,
            WebsocketBasePath = "mqtt",
            SubscribedTopics = [new SubscribedTopic { Topic = "spBv1.0/#" }]
        }
    ];
}
