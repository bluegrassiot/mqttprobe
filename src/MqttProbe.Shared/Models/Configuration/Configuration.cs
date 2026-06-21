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
}

public class UiPreferences
{
    public bool FontAccessible { get; set; } = true;
    public string Theme { get; set; } = "dark";
    public string FontFamily { get; set; } = "OpenDyslexic";
    public bool AutoResubscribe { get; set; } = true;

    // Identifiers of onboarding hints the operator has dismissed. Persisting these
    // keeps the "Start here" banners from re-appearing as chrome noise on every visit.
    public List<string> DismissedHints { get; set; } = [];
}

public class AppConfiguration
{
    public List<Connection> Connections { get; set; } = [];
    public Auth Auth { get; set; } = new();
    public PerformanceSettings Performance { get; set; } = new();
    public UiPreferences Ui { get; set; } = new();
    public List<ChartConfiguration> Charts { get; set; } = [];
    public EmulatorDocument Emulators { get; set; } = new();
}
