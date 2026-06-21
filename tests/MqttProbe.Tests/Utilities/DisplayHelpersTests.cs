using MqttProbe.Utilities;

namespace MqttProbe.Shared.Tests.Utilities;

[TestFixture]
public class DisplayHelpersTests
{
    [Test]
    public void GetRelativeTime_Null_ReturnsEmDash()
    {
        DisplayHelpers.GetRelativeTime(null).Should().Be("—");
    }

    [Test]
    public void GetRelativeTime_UnderOneMinute_ReturnsSeconds()
    {
        DisplayHelpers.GetRelativeTime(DateTime.UtcNow.AddSeconds(-30)).Should().EndWith("s ago");
    }

    [Test]
    public void GetRelativeTime_UnderOneHour_ReturnsMinutes()
    {
        DisplayHelpers.GetRelativeTime(DateTime.UtcNow.AddMinutes(-5)).Should().EndWith("m ago");
    }

    [Test]
    public void GetRelativeTime_UnderOneDay_ReturnsHours()
    {
        DisplayHelpers.GetRelativeTime(DateTime.UtcNow.AddHours(-3)).Should().EndWith("h ago");
    }

    [Test]
    public void GetRelativeTime_OverOneDay_ReturnsDays()
    {
        DisplayHelpers.GetRelativeTime(DateTime.UtcNow.AddDays(-2)).Should().Be("2d ago");
    }
}
