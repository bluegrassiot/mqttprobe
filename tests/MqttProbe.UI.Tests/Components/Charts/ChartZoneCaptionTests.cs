using MqttProbe.UI.Services.Formatting;

namespace MqttProbe.UI.Tests.Components.Charts;

[TestFixture]
public class ChartZoneCaptionTests
{
    [Test]
    public void SingleAbbreviation_ShowsTheShortZoneAndFullDetails()
    {
        var result = Result(Item("EST", "Eastern Standard Time", "UTC-05:00"));

        var description = ChartZoneCaption.Describe(result);

        description.Visible.Should().Be("EST");
        description.Details.Should()
            .Contain("America/New_York")
            .And.Contain("Eastern Standard Time")
            .And.Contain("UTC-05:00");
    }

    [Test]
    public void DistinctAbbreviations_ShowBothSidesInChronologicalOrder()
    {
        var result = Result(
            Item("EDT", "Eastern Daylight Time", "UTC-04:00"),
            Item("EST", "Eastern Standard Time", "UTC-05:00"));

        var description = ChartZoneCaption.Describe(result);

        description.Visible.Should().Be("EDT / EST");
        description.Details.Should()
            .Contain("America/New_York")
            .And.Contain("Eastern Daylight Time (UTC-04:00)")
            .And.Contain("Eastern Standard Time (UTC-05:00)");
    }

    [Test]
    public void RepeatedAbbreviation_AppearsOnce()
    {
        var result = Result(
            Item("EST", "Eastern Standard Time", "UTC-05:00"),
            Item("EST", "Eastern Standard Time", "UTC-05:00"));

        var description = ChartZoneCaption.Describe(result);

        description.Visible.Should().Be("EST");
        description.Details.Should().Be("America/New_York · Eastern Standard Time (UTC-05:00)");
    }

    [Test]
    public void NoItems_FallsBackToTheZoneLabelRatherThanAnEmptyCaption()
    {
        var description = ChartZoneCaption.Describe(
            new DeviceTimeFormatResult("UTC", "Coordinated Universal Time", []));

        description.Visible.Should().Be("UTC");
        description.Details.Should().Contain("Coordinated Universal Time");
    }

    [Test]
    public void MissingResult_ProducesNoCaption()
    {
        var description = ChartZoneCaption.Describe(null);

        description.Visible.Should().BeEmpty();
        description.Details.Should().BeEmpty();
    }

    private static DeviceTimeFormatResult Result(params DeviceTimeFormatItem[] items) =>
        new("America/New_York", "Eastern Standard Time", [.. items]);

    private static DeviceTimeFormatItem Item(string abbreviation, string name, string offset) =>
        new(Guid.NewGuid(), "", "", "", abbreviation, name, offset);
}
