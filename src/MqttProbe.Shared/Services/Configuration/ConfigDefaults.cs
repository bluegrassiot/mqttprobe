using System.Text.Json;
using MqttProbe.Models.Configuration;
using MqttProbe.Models.Mqtt;

namespace MqttProbe.Services.Configuration;

internal static class ConfigDefaults
{
    // Guards against explicit nulls in the config file; the model's own initializers only
    // cover properties the JSON omits entirely.
    public static void Normalize(AppConfiguration config, string? legacyJson = null)
    {
        // Sparkplug settings: migrate from legacy ui keys when section is absent.
        if (config.Sparkplug is null)
        {
            config.Sparkplug = new SparkplugSettings();

            if (legacyJson is not null)
            {
                try
                {
                    using var doc = JsonDocument.Parse(legacyJson);
                    if (doc.RootElement.TryGetProperty("ui", out var ui)
                        && ui.ValueKind == JsonValueKind.Object)
                    {
                        if (ui.TryGetProperty("autoRequestSparkplugRebirth", out var arr))
                            config.Sparkplug.AutoRequestRebirth = arr.GetBoolean();
                        if (ui.TryGetProperty("enrichSparkplugAliasNames", out var ea))
                            config.Sparkplug.EnrichAliasNames = ea.GetBoolean();
                        if (ui.TryGetProperty("allowNodeReboot", out var ar))
                            config.Sparkplug.AllowNodeReboot = ar.GetBoolean();
                    }
                }
                catch (JsonException)
                {
                    // Malformed JSON: keep defaults.
                }
            }
        }

        config.Sparkplug.RebirthCooldownSeconds =
            Math.Clamp(config.Sparkplug.RebirthCooldownSeconds, 5, 600);
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
