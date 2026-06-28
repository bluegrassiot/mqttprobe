using MqttProbe.Models.Chart;
using MqttProbe.Services.Chart;

namespace MqttProbe.Shared.Tests.Components.Charts;

[TestFixture]
public class TimeWindowFilterTests
{
    private static readonly DateTime _now = new(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    [Test]
    public void Apply_NullTimeWindow_ReturnsAllPoints()
    {
        var allPoints = new List<ChartDataPoint>
        {
            new(_now.AddMinutes(-60), 1.0),
            new(_now.AddMinutes(-10), 2.0),
            new(_now, 3.0)
        };

        var filtered = TimeWindowFilter.Apply(allPoints, timeWindowMinutes: null, _now);

        filtered.Should().HaveCount(3);
        filtered.Should().Contain(p => p.Value == 1.0);
        filtered.Should().Contain(p => p.Value == 2.0);
        filtered.Should().Contain(p => p.Value == 3.0);
    }

    [Test]
    public void Apply_PositiveTimeWindow_ExcludesOldAndIncludesRecent()
    {
        var allPoints = new List<ChartDataPoint>
        {
            new(_now.AddMinutes(-10), 1.0),
            new(_now.AddMinutes(-2), 2.0),
            new(_now, 3.0)
        };

        var filtered = TimeWindowFilter.Apply(allPoints, timeWindowMinutes: 5, _now);

        filtered.Should().HaveCount(2);
        filtered.Should().Contain(p => p.Value == 2.0);
        filtered.Should().Contain(p => p.Value == 3.0);
        filtered.Should().NotContain(p => p.Value == 1.0);
    }

    [Test]
    public void Apply_ExactCutoff_IsIncluded()
    {
        // With deterministic "now", the cutoff is exact: now - 5 minutes.
        // A point exactly AT the cutoff must be included (>= comparison).
        var cutoff = _now.AddMinutes(-5);
        var allPoints = new List<ChartDataPoint>
        {
            new(cutoff.AddTicks(-1), 1.0),   // just before cutoff — excluded
            new(cutoff, 2.0),                 // exactly at cutoff — included
            new(cutoff.AddTicks(1), 3.0),     // just after cutoff — included
            new(_now, 4.0)                    // current — included
        };

        var filtered = TimeWindowFilter.Apply(allPoints, timeWindowMinutes: 5, _now);

        filtered.Should().HaveCount(3);
        filtered.Should().Contain(p => p.Value == 2.0);
        filtered.Should().Contain(p => p.Value == 3.0);
        filtered.Should().Contain(p => p.Value == 4.0);
        filtered.Should().NotContain(p => p.Value == 1.0);
    }

    [Test]
    public void Apply_ZeroNormalizedConfig_ReturnsAllPoints()
    {
        // The ChartConfiguration model normalizes TimeWindowMinutes <= 0 to null.
        // When null, the filter returns all points (no time window).
        var config = new ChartConfiguration { TimeWindowMinutes = 0 };
        config.TimeWindowMinutes.Should().BeNull(
            "model normalizes non-positive values to null");

        var allPoints = new List<ChartDataPoint>
        {
            new(_now.AddMinutes(-60), 1.0),
            new(_now.AddMinutes(-10), 2.0),
            new(_now, 3.0)
        };

        var filtered = TimeWindowFilter.Apply(allPoints, config.TimeWindowMinutes, _now);

        filtered.Should().HaveCount(3);
    }

    [Test]
    public void Apply_EmptyPoints_ReturnsEmpty()
    {
        var filtered = TimeWindowFilter.Apply([], timeWindowMinutes: 5, _now);

        filtered.Should().BeEmpty();
    }

    [Test]
    public void Apply_LargeTimeWindow_IncludesAllRecentPoints()
    {
        var allPoints = new List<ChartDataPoint>
        {
            new(_now.AddMinutes(-59), 1.0),
            new(_now.AddMinutes(-30), 2.0),
            new(_now, 3.0),
            new(_now.AddMinutes(-61), 4.0),
        };

        var filtered = TimeWindowFilter.Apply(allPoints, timeWindowMinutes: 60, _now);

        filtered.Should().HaveCount(3);
        filtered.Should().Contain(p => p.Value == 1.0);
        filtered.Should().Contain(p => p.Value == 2.0);
        filtered.Should().Contain(p => p.Value == 3.0);
        filtered.Should().NotContain(p => p.Value == 4.0);
    }
}
