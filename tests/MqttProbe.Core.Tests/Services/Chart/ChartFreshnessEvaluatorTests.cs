using MqttProbe.Core.Models.Chart;
using MqttProbe.Core.Services.Chart;

namespace MqttProbe.Core.Tests.Services.Chart;

[TestFixture]
public class ChartFreshnessEvaluatorTests
{
    private static readonly DateTime _now = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);

    [Test]
    public void SeriesThatNeverReceivedData_IsWaiting()
    {
        var seriesId = Guid.NewGuid();

        var report = ChartFreshnessEvaluator.Evaluate(
            [new ChartSeriesReceipt(seriesId, null)], 10, _now);

        report.Series.Should().ContainSingle();
        report.Series[0].State.Should().Be(ChartSeriesFreshnessState.Waiting);
        report.Series[0].Age.Should().BeNull();
        report.State.Should().Be(ChartFreshnessState.Waiting);
    }

    [Test]
    public void AgeExactlyTheTimeout_IsFresh_AndOneTickBeyond_IsStale()
    {
        var seriesId = Guid.NewGuid();

        var atBoundary = ChartFreshnessEvaluator.Evaluate(
            [new ChartSeriesReceipt(seriesId, _now.AddSeconds(-10))], 10, _now);

        atBoundary.Series[0].State.Should().Be(ChartSeriesFreshnessState.Fresh);
        atBoundary.State.Should().Be(ChartFreshnessState.Live);

        var pastBoundary = ChartFreshnessEvaluator.Evaluate(
            [new ChartSeriesReceipt(seriesId, _now.AddSeconds(-10).AddTicks(-1))], 10, _now);

        pastBoundary.Series[0].State.Should().Be(ChartSeriesFreshnessState.Stale);
        pastBoundary.State.Should().Be(ChartFreshnessState.Stale);
    }

    [Test]
    public void NewerReceipt_MakesAStaleSeriesFreshAgain()
    {
        var seriesId = Guid.NewGuid();

        var stale = ChartFreshnessEvaluator.Evaluate(
            [new ChartSeriesReceipt(seriesId, _now.AddSeconds(-30))], 10, _now);
        stale.Series[0].State.Should().Be(ChartSeriesFreshnessState.Stale);

        var recovered = ChartFreshnessEvaluator.Evaluate(
            [new ChartSeriesReceipt(seriesId, _now.AddSeconds(-1))], 10, _now);

        recovered.Series[0].State.Should().Be(ChartSeriesFreshnessState.Fresh);
        recovered.Series[0].Age.Should().Be(TimeSpan.FromSeconds(1));
        recovered.State.Should().Be(ChartFreshnessState.Live);
    }

    [Test]
    public void MixedRateSeries_EvaluateIndependently_AndSummaryReportsSomeStale()
    {
        var freshId = Guid.NewGuid();
        var staleId = Guid.NewGuid();

        var report = ChartFreshnessEvaluator.Evaluate(
            [
                new ChartSeriesReceipt(freshId, _now.AddSeconds(-2)),
                new ChartSeriesReceipt(staleId, _now.AddSeconds(-40))
            ],
            10,
            _now);

        report.Series.Single(s => s.SeriesId == freshId).State
            .Should().Be(ChartSeriesFreshnessState.Fresh);
        report.Series.Single(s => s.SeriesId == staleId).State
            .Should().Be(ChartSeriesFreshnessState.Stale);
        report.State.Should().Be(ChartFreshnessState.SomeStale);
        report.FreshSeriesCount.Should().Be(1);
        report.StaleSeriesCount.Should().Be(1);
    }

    [Test]
    public void MixedWaitingFreshAndStaleSeries_StayDistinguishable()
    {
        var waitingId = Guid.NewGuid();
        var freshId = Guid.NewGuid();
        var staleId = Guid.NewGuid();

        var report = ChartFreshnessEvaluator.Evaluate(
            [
                new ChartSeriesReceipt(waitingId, null),
                new ChartSeriesReceipt(freshId, _now.AddSeconds(-2)),
                new ChartSeriesReceipt(staleId, _now.AddSeconds(-40))
            ],
            10,
            _now);

        report.State.Should().Be(ChartFreshnessState.Mixed);
        report.WaitingSeriesCount.Should().Be(1);
        report.FreshSeriesCount.Should().Be(1);
        report.StaleSeriesCount.Should().Be(1);
        report.Series.Single(s => s.SeriesId == waitingId).Age.Should().BeNull();
        report.Series.Single(s => s.SeriesId == staleId).Age.Should().Be(TimeSpan.FromSeconds(40));
    }

    [Test]
    public void WaitingSeriesAlongsideFreshSeries_IsMixedNotLive()
    {
        var report = ChartFreshnessEvaluator.Evaluate(
            [
                new ChartSeriesReceipt(Guid.NewGuid(), null),
                new ChartSeriesReceipt(Guid.NewGuid(), _now.AddSeconds(-1))
            ],
            10,
            _now);

        report.State.Should().Be(ChartFreshnessState.Mixed);
    }

    [Test]
    public void DetectionOff_NeverReportsOldDataAsFreshOrStale_AndKeepsAge()
    {
        var oldId = Guid.NewGuid();
        var waitingId = Guid.NewGuid();

        var report = ChartFreshnessEvaluator.Evaluate(
            [
                new ChartSeriesReceipt(oldId, _now.AddHours(-1)),
                new ChartSeriesReceipt(waitingId, null)
            ],
            null,
            _now);

        var old = report.Series.Single(s => s.SeriesId == oldId);
        old.State.Should().Be(ChartSeriesFreshnessState.NotTracked);
        old.Age.Should().Be(TimeSpan.FromHours(1));
        report.Series.Single(s => s.SeriesId == waitingId).State
            .Should().Be(ChartSeriesFreshnessState.Waiting);
        report.State.Should().Be(ChartFreshnessState.Disabled);
        report.FreshSeriesCount.Should().Be(0);
        report.StaleSeriesCount.Should().Be(0);
        report.NotTrackedSeriesCount.Should().Be(1);
        report.WaitingSeriesCount.Should().Be(1);
    }

    [Test]
    public void DetectionOff_WithNoReceivedData_StaysWaiting()
    {
        var report = ChartFreshnessEvaluator.Evaluate(
            [new ChartSeriesReceipt(Guid.NewGuid(), null)], null, _now);

        report.State.Should().Be(ChartFreshnessState.Waiting);
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

        report.Series[0].State.Should().Be(ChartSeriesFreshnessState.NotTracked);
        report.State.Should().Be(ChartFreshnessState.Disabled);
    }

    [Test]
    public void VeryLargeFiniteTimeout_KeepsDataFresh()
    {
        var report = ChartFreshnessEvaluator.Evaluate(
            [new ChartSeriesReceipt(Guid.NewGuid(), _now.AddHours(-1))], double.MaxValue, _now);

        report.Series[0].State.Should().Be(ChartSeriesFreshnessState.Fresh);
    }
}
