using MqttProbe.Models.Chart;
using MqttProbe.Models.Emulation;
using MqttProbe.Models.Mqtt;

namespace MqttProbe.Models.Configuration;

public class Auth
{
    public string Username { get; set; } = string.Empty;
    // PBKDF2-SHA256: "<base64-salt>:<base64-hash>:<iterations>"
    public string PasswordHash { get; set; } = string.Empty;
}

public class PerformanceSettings
{
    public int MaxStoredMessages { get; set; } = 10_000;

    public int MaxMessagesPerSecond { get; set; } = 50_000;

    public int MaxDisplayMessages { get; set; } = 500;

    public int MaxTopicNodes { get; set; } = 10_000;
}

public class SparkplugSettings
{
    public bool AutoRequestRebirth { get; set; }
    public int RebirthCooldownSeconds { get; set; } = 30;
    public bool EnrichAliasNames { get; set; } = true;
    public bool AllowNodeReboot { get; set; }
}

public static class FontProfiles
{
    public const string Standard = "standard";
    public const string Accessible = "accessible";

    public static string Normalize(string? value) =>
        string.Equals(value, Accessible, StringComparison.OrdinalIgnoreCase) ? Accessible : Standard;

    public static bool IsAccessible(string? value) =>
        string.Equals(Normalize(value), Accessible, StringComparison.OrdinalIgnoreCase);
}

public class UiPreferences
{
    public string FontProfile { get; set; } = FontProfiles.Standard;
    public string Theme { get; set; } = "dark";
    public bool AutoResubscribe { get; set; } = true;

    public List<string> DismissedHints { get; set; } = [];
}

public class AppConfiguration
{
    public List<Connection> Connections { get; set; } = [];
    public Auth Auth { get; set; } = new();
    public PerformanceSettings Performance { get; set; } = new();
    public UiPreferences Ui { get; set; } = new();
    public SparkplugSettings? Sparkplug { get; set; }

    public Dictionary<Guid, List<ChartConfiguration>> ChartsByConnection { get; set; } = [];

    public Dictionary<Guid, EmulatorDocument> EmulatorsByConnection { get; set; } = [];
}
