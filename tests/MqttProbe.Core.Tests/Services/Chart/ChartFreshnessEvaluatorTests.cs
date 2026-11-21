using MqttProbe.Core.Models.Chart;
using MqttProbe.Core.Services.Chart;

namespace MqttProbe.Core.Tests.Services.Chart;

[TestFixture]
public class ChartFreshnessEvaluatorTests
{
    private static readonly DateTime _now = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);

    [Test]
    public void NoSeries_IsWaitingWithoutReceipt()
    {
        var report = ChartFreshnessEvaluator.Evaluate([], 10, _now);

        report.State.Should().Be(ChartFreshnessState.Waiting);
        report.LastReceivedUtc.Should().BeNull();
        report.Age.Should().BeNull();
    }

    [Test]
    public void SeriesWithoutData_IsWaitingWithoutReceipt()
    {
        var report = ChartFreshnessEvaluator.Evaluate(
            [new ChartSeriesReceipt(Guid.NewGuid(), null)], 10, _now);

        report.State.Should().Be(ChartFreshnessState.Waiting);
        report.LastReceivedUtc.Should().BeNull();
        report.Age.Should().BeNull();
    }

    [Test]
    public void OldAndFreshSeries_NewestReceiptMakesTheChartLive()
    {
        var oldId = Guid.NewGuid();
        var freshId = Guid.NewGuid();
        var freshReceipt = _now.AddSeconds(-2);

        var report = ChartFreshnessEvaluator.Evaluate(
            [
                new ChartSeriesReceipt(oldId, _now.AddSeconds(-40)),
                new ChartSeriesReceipt(freshId, freshReceipt)
            ],
            10,
            _now);

        report.State.Should().Be(ChartFreshnessState.Live);
        report.LastReceivedUtc.Should().Be(freshReceipt);
        report.Age.Should().Be(TimeSpan.FromSeconds(2));
    }

    [Test]
    public void NewerReceiptFromEitherSeries_KeepsTheChartLive()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();

        var secondAdvances = ChartFreshnessEvaluator.Evaluate(
            [
                new ChartSeriesReceipt(firstId, _now.AddSeconds(-8)),
                new ChartSeriesReceipt(secondId, _now.AddSeconds(-1))
            ],
            10,
            _now);

        secondAdvances.State.Should().Be(ChartFreshnessState.Live);
        secondAdvances.LastReceivedUtc.Should().Be(_now.AddSeconds(-1));

        var firstAdvances = ChartFreshnessEvaluator.Evaluate(
            [
                new ChartSeriesReceipt(firstId, _now.AddSeconds(-1)),
                new ChartSeriesReceipt(secondId, _now.AddSeconds(-8))
            ],
            10,
            _now);

        firstAdvances.State.Should().Be(ChartFreshnessState.Live);
        firstAdvances.LastReceivedUtc.Should().Be(_now.AddSeconds(-1));
    }

    [Test]
    public void AllSeriesOld_IsStaleWithTheNewestReceipt()
    {
        var newest = _now.AddSeconds(-30);

        var report = ChartFreshnessEvaluator.Evaluate(
            [
                new ChartSeriesReceipt(Guid.NewGuid(), _now.AddSeconds(-40)),
                new ChartSeriesReceipt(Guid.NewGuid(), newest)
            ],
            10,
            _now);

        report.State.Should().Be(ChartFreshnessState.Stale);
        report.LastReceivedUtc.Should().Be(newest);
        report.Age.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Test]
    public void WaitingSeriesBesideFreshData_DoesNotChangeTheLiveState()
    {
        var report = ChartFreshnessEvaluator.Evaluate(
            [
                new ChartSeriesReceipt(Guid.NewGuid(), null),
                new ChartSeriesReceipt(Guid.NewGuid(), _now.AddSeconds(-1))
            ],
            10,
            _now);

        report.State.Should().Be(ChartFreshnessState.Live);
    }

    [Test]
    public void AgeAtTheTimeout_IsLive_AndOneTickBeyond_IsStale()
    {
        var atBoundary = ChartFreshnessEvaluator.Evaluate(
            [new ChartSeriesReceipt(Guid.NewGuid(), _now.AddSeconds(-10))], 10, _now);

        atBoundary.State.Should().Be(ChartFreshnessState.Live);
        atBoundary.Age.Should().Be(TimeSpan.FromSeconds(10));

        var pastBoundary = ChartFreshnessEvaluator.Evaluate(
            [new ChartSeriesReceipt(Guid.NewGuid(), _now.AddSeconds(-10).AddTicks(-1))],
            10,
            _now);

        pastBoundary.State.Should().Be(ChartFreshnessState.Stale);
    }

    [Test]
    public void ReceiptAheadOfNow_IsLive()
    {
        var ahead = _now.AddSeconds(5);

        var report = ChartFreshnessEvaluator.Evaluate(
            [new ChartSeriesReceipt(Guid.NewGuid(), ahead)], 10, _now);

        report.State.Should().Be(ChartFreshnessState.Live);
        report.LastReceivedUtc.Should().Be(ahead);
        report.Age.Should().Be(TimeSpan.FromSeconds(-5));
    }

    [Test]
    public void TimeoutOff_WithReceivedData_IsDisabledWithoutReceipt()
    {
        var report = ChartFreshnessEvaluator.Evaluate(
            [new ChartSeriesReceipt(Guid.NewGuid(), _now.AddSeconds(-1))], null, _now);

        report.State.Should().Be(ChartFreshnessState.Disabled);
        report.LastReceivedUtc.Should().BeNull();
        report.Age.Should().BeNull();
    }

    [Test]
    public void TimeoutOff_WithoutReceivedData_IsDisabled()
    {
        var report = ChartFreshnessEvaluator.Evaluate(
            [new ChartSeriesReceipt(Guid.NewGuid(), null)], null, _now);

        report.State.Should().Be(ChartFreshnessState.Disabled);
        report.LastReceivedUtc.Should().BeNull();
        report.Age.Should().BeNull();
    }

    [Test]
    public void PositiveFiniteTimeout_IsValid_AndNullMeansOff()
    {
        ChartFreshnessEvaluator.IsValidTimeout(null).Should().BeTrue();
        ChartFreshnessEvaluator.IsValidTimeout(0.5).Should().BeTrue();
        ChartFreshnessEvaluator.IsValidTimeout(10).Should().BeTrue();
    }

    [TestCase(0d)]
    [TestCase(-1d)]
    [TestCase(double.NaN)]
    [TestCase(double.PositiveInfinity)]
    public void InvalidTimeout_IsRejected_AndTreatedAsOff(double invalid)
    {
        ChartFreshnessEvaluator.IsValidTimeout(invalid).Should().BeFalse();

        var report = ChartFreshnessEvaluator.Evaluate(
            [new ChartSeriesReceipt(Guid.NewGuid(), _now.AddSeconds(-1))], invalid, _now);

        report.State.Should().Be(ChartFreshnessState.Disabled);
        report.LastReceivedUtc.Should().BeNull();
        report.Age.Should().BeNull();
    }

    [Test]
    public void VeryLargeFiniteTimeout_KeepsDataLive()
    {
        var report = ChartFreshnessEvaluator.Evaluate(
            [new ChartSeriesReceipt(Guid.NewGuid(), _now.AddHours(-1))], double.MaxValue, _now);

        report.State.Should().Be(ChartFreshnessState.Live);
    }
}
