using System.Globalization;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Mqtt;

namespace MqttProbe.UI.Components.Browser;

internal sealed record MessageTimeFormatItem(
    Guid Key,
    string TimeText,
    string DateText,
    string FullText,
    string ZoneAbbreviation,
    string ZoneName,
    string OffsetLabel);

internal sealed record MessageTimeFormatResult(
    string ZoneLabel,
    string ZoneName,
    List<MessageTimeFormatItem>? Items);

public sealed record MessageTimePresentation(
    string HeaderLabel,
    string MenuHint,
    bool IsLocal,
    bool Is12Hour,
    bool SameTopic)
{
    internal string Mode { get; init; } = MessageTimePreferences.ModeClock;
    internal string? HeaderMeta { get; init; }
    internal string? HeaderMetaTitle { get; init; }
    internal Func<MqttMessage, TimeSpan?>? DurationSelector { get; init; }
    internal string? DurationTitleContext { get; init; }
    internal string? MissingDurationTitle { get; init; }

    internal bool IsClock => Mode == MessageTimePreferences.ModeClock;
    internal bool IsElapsed => Mode == MessageTimePreferences.ModeElapsed;
    internal bool IsDelta => Mode == MessageTimePreferences.ModeDelta;

    internal TimeSpan? DurationFor(MqttMessage message) => DurationSelector?.Invoke(message);

    internal string DurationTitle(TimeSpan? value) =>
        value is { } duration
            ? $"{DurationTitleContext}: {MessageTimeDisplay.FormatDuration(duration)}"
            : MissingDurationTitle ?? string.Empty;
}

internal static class MessageTimeDisplay
{
    internal const string MissingMarker = "—";
    internal const string PendingMarker = "…";
    internal const string PendingTitle = "Waiting for local time";

    internal static MessageTimePresentation Describe(MessageTimePreferences preferences)
    {
        var isLocal = preferences.Zone == MessageTimePreferences.ZoneLocal;
        var is12Hour = preferences.ClockFormat == MessageTimePreferences.ClockFormat12Hour;
        var sameTopic = preferences.DeltaScope == MessageTimePreferences.DeltaScopeTopic;

        return preferences.Mode switch
        {
            MessageTimePreferences.ModeElapsed => new MessageTimePresentation(
                "Elapsed", "Elapsed starts at the first message in the buffer.", isLocal, is12Hour, sameTopic)
            {
                Mode = MessageTimePreferences.ModeElapsed,
                HeaderMeta = "from first message",
                HeaderMetaTitle = "Elapsed time starts at the first message in the buffer",
                DurationSelector = static message => message.ElapsedSinceBufferStart,
                DurationTitleContext = "Elapsed since the first message",
                MissingDurationTitle = "No reference message"
            },
            MessageTimePreferences.ModeDelta => new MessageTimePresentation(
                "Delta", "Delta compares with the previous message in receive order.", isLocal, is12Hour, sameTopic)
            {
                Mode = MessageTimePreferences.ModeDelta,
                HeaderMeta = sameTopic ? "same topic" : "all topics",
                HeaderMetaTitle = "Delta uses the previous message in receive order",
                DurationSelector = sameTopic
                    ? static message => message.SincePreviousSameTopic
                    : static message => message.SincePreviousAllTopics,
                DurationTitleContext = sameTopic
                    ? "Delta from the previous message on the same topic"
                    : "Delta from the previous message on any topic",
                MissingDurationTitle = "No previous message"
            },
            _ => new MessageTimePresentation(
                "Time",
                isLocal ? "Local uses this device's time zone." : "Clock shows when each message arrived.",
                isLocal, is12Hour, sameTopic)
        };
    }

    internal static string FormatDuration(TimeSpan? value)
    {
        if (value is not { } duration)
            return MissingMarker;

        var negative = duration < TimeSpan.Zero;
        var magnitude = negative ? duration.Negate() : duration;
        var sign = negative ? "-" : "";

        if (magnitude == TimeSpan.Zero)
            return "0 ms";
        if (magnitude.TotalSeconds < 1)
            return $"{sign}{magnitude.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)} ms";
        if (magnitude.TotalMinutes < 1)
            return $"{sign}{magnitude.Seconds}.{magnitude.Milliseconds:000} s";
        if (magnitude.TotalHours < 1)
            return $"{sign}{magnitude.Minutes}m {magnitude.Seconds:00}.{magnitude.Milliseconds:000}s";
        if (magnitude.TotalDays < 1)
            return $"{sign}{magnitude.Hours}h {magnitude.Minutes:00}m {magnitude.Seconds:00}.{magnitude.Milliseconds:000}s";

        return $"{sign}{magnitude.Days}d {magnitude.Hours:00}h {magnitude.Minutes:00}m {magnitude.Seconds:00}.{magnitude.Milliseconds:000}s";
    }

    internal static MessageTimeFormatItem FallbackClock(MqttMessage message, MessageTimePreferences preferences)
    {
        var received = DateTime.SpecifyKind(message.DateTimeReceived, DateTimeKind.Utc);
        var timeText = received.ToString(
            preferences.ClockFormat == MessageTimePreferences.ClockFormat12Hour
                ? "hh:mm:ss.fff tt"
                : "HH:mm:ss.fff",
            CultureInfo.InvariantCulture);
        var dateText = received.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        return new MessageTimeFormatItem(
            message.Id,
            timeText,
            dateText,
            $"{dateText} {timeText} UTC",
            "UTC",
            "Coordinated Universal Time",
            "UTC+00:00");
    }

    internal static long EpochMillis(DateTime received) =>
        new DateTimeOffset(DateTime.SpecifyKind(received, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
}
