namespace MqttProbe.Utilities;

public static class DisplayHelpers
{
    public static string GetRelativeTime(DateTime? utcTime)
    {
        if (utcTime is null)
            return "—";

        var elapsed = DateTime.UtcNow - utcTime.Value;
        if (elapsed.TotalSeconds < 60)
            return $"{(int)elapsed.TotalSeconds}s ago";
        if (elapsed.TotalMinutes < 60)
            return $"{(int)elapsed.TotalMinutes}m ago";
        if (elapsed.TotalHours < 24)
            return $"{(int)elapsed.TotalHours}h ago";
        return $"{(int)elapsed.TotalDays}d ago";
    }
}
