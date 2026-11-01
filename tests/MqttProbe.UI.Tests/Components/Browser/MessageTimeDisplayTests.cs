using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Mqtt;

namespace MqttProbe.UI.Tests.Components.Browser;

[TestFixture]
public class MessageTimeDisplayTests
{
    [Test]
    public void FormatDuration_KeepsUnitsAndMillisecondsClear()
    {
        MessageTimeDisplay.FormatDuration(TimeSpan.Zero).Should().Be("0 ms");
        MessageTimeDisplay.FormatDuration(TimeSpan.FromMilliseconds(250)).Should().Be("250 ms");
        MessageTimeDisplay.FormatDuration(TimeSpan.FromTicks(5_000)).Should().Be("0.5 ms");
        MessageTimeDisplay.FormatDuration(TimeSpan.FromMilliseconds(1250)).Should().Be("1.250 s");
        MessageTimeDisplay.FormatDuration(TimeSpan.FromSeconds(65.5)).Should().Be("1m 05.500s");
        MessageTimeDisplay.FormatDuration(new TimeSpan(0, 2, 3, 4, 5)).Should().Be("2h 03m 04.005s");
        MessageTimeDisplay.FormatDuration(new TimeSpan(1, 2, 0, 0, 0)).Should().Be("1d 02h 00m 00.000s");
    }

    [Test]
    public void FormatDuration_SignsNegativeIntervalsInsteadOfClamping()
    {
        MessageTimeDisplay.FormatDuration(TimeSpan.FromMilliseconds(-1250)).Should().Be("-1.250 s");
        MessageTimeDisplay.FormatDuration(TimeSpan.FromSeconds(-65.5)).Should().Be("-1m 05.500s");
    }

    [Test]
    public void FormatDuration_LeavesAMissingValueEmpty()
    {
        MessageTimeDisplay.FormatDuration(null).Should().Be(MessageTimeDisplay.MissingMarker);
    }

    [Test]
    public void Describe_ClockLeavesTheDynamicHeaderDetailToTheParent()
    {
        var utc = MessageTimeDisplay.Describe(new MessageTimePreferences());

        utc.IsClock.Should().BeTrue();
        utc.IsElapsed.Should().BeFalse();
        utc.IsDelta.Should().BeFalse();
        utc.HeaderLabel.Should().Be("Time");
        utc.HeaderMeta.Should().BeNull();
        utc.HeaderMetaTitle.Should().BeNull();
        utc.MenuHint.Should().Be("Clock shows when each message arrived.");
        utc.IsLocal.Should().BeFalse();
        utc.Is12Hour.Should().BeFalse();

        var localTwelveHour = MessageTimeDisplay.Describe(new MessageTimePreferences
        {
            Zone = MessageTimePreferences.ZoneLocal,
            ClockFormat = MessageTimePreferences.ClockFormat12Hour
        });

        localTwelveHour.IsLocal.Should().BeTrue();
        localTwelveHour.Is12Hour.Should().BeTrue();
        localTwelveHour.MenuHint.Should().Be("Local uses this device's time zone.");
    }

    [Test]
    public void Describe_ElapsedOwnsItsDurationRoutingAndWording()
    {
        var presentation = MessageTimeDisplay.Describe(new MessageTimePreferences
        {
            Mode = MessageTimePreferences.ModeElapsed
        });
        var message = new MqttMessage
        {
            ElapsedSinceBufferStart = TimeSpan.FromSeconds(65.5),
            SincePreviousAllTopics = TimeSpan.FromSeconds(1),
            SincePreviousSameTopic = TimeSpan.FromSeconds(2)
        };

        presentation.IsElapsed.Should().BeTrue();
        presentation.HeaderLabel.Should().Be("Elapsed");
        presentation.HeaderMeta.Should().Be("from first message");
        presentation.HeaderMetaTitle.Should().Be("Elapsed time starts at the first message in the buffer");
        presentation.MenuHint.Should().Be("Elapsed starts at the first message in the buffer.");
        presentation.DurationFor(message).Should().Be(TimeSpan.FromSeconds(65.5));
        presentation.DurationTitle(null).Should().Be("No reference message");
        presentation.DurationTitle(TimeSpan.FromSeconds(65.5))
            .Should().Be("Elapsed since the first message: 1m 05.500s");
    }

    [Test]
    public void Describe_DeltaResolvesScopeStringsSelectorAndTitleTogether()
    {
        var all = MessageTimeDisplay.Describe(new MessageTimePreferences
        {
            Mode = MessageTimePreferences.ModeDelta,
            Zone = MessageTimePreferences.ZoneLocal,
            ClockFormat = MessageTimePreferences.ClockFormat12Hour
        });
        var same = MessageTimeDisplay.Describe(new MessageTimePreferences
        {
            Mode = MessageTimePreferences.ModeDelta,
            DeltaScope = MessageTimePreferences.DeltaScopeTopic
        });
        var message = new MqttMessage
        {
            ElapsedSinceBufferStart = TimeSpan.FromSeconds(3),
            SincePreviousAllTopics = TimeSpan.FromSeconds(1.25),
            SincePreviousSameTopic = TimeSpan.FromSeconds(1.75)
        };

        all.SameTopic.Should().BeFalse();
        all.HeaderMeta.Should().Be("all topics");
        all.HeaderMetaTitle.Should().Be("Delta uses the previous message in receive order");
        all.MenuHint.Should().Be("Delta compares with the previous message in receive order.");
        all.DurationFor(message).Should().Be(TimeSpan.FromSeconds(1.25));
        all.DurationTitle(null).Should().Be("No previous message");
        all.DurationTitle(TimeSpan.FromSeconds(1.25))
            .Should().Be("Delta from the previous message on any topic: 1.250 s");

        same.SameTopic.Should().BeTrue();
        same.HeaderMeta.Should().Be("same topic");
        same.DurationFor(message).Should().Be(TimeSpan.FromSeconds(1.75));
        same.DurationTitle(TimeSpan.FromSeconds(1.75))
            .Should().Be("Delta from the previous message on the same topic: 1.750 s");
    }

    [Test]
    public void DurationTitle_SaysWhenThereIsNoPredecessor()
    {
        var delta = new MessageTimePreferences { Mode = MessageTimePreferences.ModeDelta };
        var elapsed = new MessageTimePreferences { Mode = MessageTimePreferences.ModeElapsed };

        MessageTimeDisplay.Describe(delta).DurationTitle(null).Should().Be("No previous message");
        MessageTimeDisplay.Describe(elapsed).DurationTitle(null).Should().Be("No reference message");
    }

    [Test]
    public void FallbackClock_FormatsTwelveHourWithMilliseconds()
    {
        var message = new MqttMessage
        {
            DateTimeReceived = new DateTime(2026, 10, 29, 22, 41, 7, 123, DateTimeKind.Utc)
        };
        var preferences = new MessageTimePreferences { ClockFormat = MessageTimePreferences.ClockFormat12Hour };

        var item = MessageTimeDisplay.FallbackClock(message, preferences);

        item.TimeText.Should().Be("10:41:07.123 PM");
        item.DateText.Should().Be("2026-10-29");
        item.FullText.Should().Be("2026-10-29 10:41:07.123 PM UTC");
        item.OffsetLabel.Should().Be("UTC+00:00");
    }

    [Test]
    public void FallbackClock_UsesZeroForMidnightInTwentyFourHourTime()
    {
        var message = new MqttMessage
        {
            DateTimeReceived = new DateTime(2026, 10, 29, 0, 5, 9, 7, DateTimeKind.Utc)
        };

        MessageTimeDisplay.FallbackClock(message, new MessageTimePreferences()).TimeText
            .Should().Be("00:05:09.007");
    }
}
