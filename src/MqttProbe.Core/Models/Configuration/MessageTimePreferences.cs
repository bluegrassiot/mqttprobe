namespace MqttProbe.Core.Models.Configuration;

public sealed record MessageTimePreferences
{
    public const string ModeClock = "clock";
    public const string ModeElapsed = "elapsed";
    public const string ModeDelta = "delta";
    public const string ZoneUtc = "utc";
    public const string ZoneLocal = "local";
    public const string ClockFormat24Hour = "24h";
    public const string ClockFormat12Hour = "12h";
    public const string DeltaScopeAll = "all";
    public const string DeltaScopeTopic = "topic";

    public string Mode { get; set; } = ModeClock;
    public string Zone { get; set; } = ZoneUtc;
    public string ClockFormat { get; set; } = ClockFormat24Hour;
    public string DeltaScope { get; set; } = DeltaScopeAll;

    public static MessageTimePreferences Normalize(MessageTimePreferences? preferences) => new()
    {
        Mode = Match(preferences?.Mode, ModeClock, ModeClock, ModeElapsed, ModeDelta),
        Zone = Match(preferences?.Zone, ZoneUtc, ZoneUtc, ZoneLocal),
        ClockFormat = Match(preferences?.ClockFormat, ClockFormat24Hour, ClockFormat24Hour, ClockFormat12Hour),
        DeltaScope = Match(preferences?.DeltaScope, DeltaScopeAll, DeltaScopeAll, DeltaScopeTopic)
    };

    private static string Match(string? value, string fallback, params string[] allowed) =>
        allowed.FirstOrDefault(candidate =>
            string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase))
        ?? fallback;
}
