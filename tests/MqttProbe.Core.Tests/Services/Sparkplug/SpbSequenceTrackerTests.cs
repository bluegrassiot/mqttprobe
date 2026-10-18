using MqttProbe.Core.Models.Sparkplug;

namespace MqttProbe.Core.Tests.Services.Sparkplug;

public sealed class SpbSequenceTrackerTests
{
    [Test]
    public void Observe_WraparoundAndLateSequence_ResolvesGap()
    {
        var tracker = new SpbSequenceTracker(TimeProvider.System, 5000);
        tracker.Reset(254);

        tracker.Observe(0).Should().BeTrue();
        tracker.Snapshot.State.Should().Be(SpbSequenceState.Reordering);
        tracker.Snapshot.MissingSequences.Should().Equal(255);

        tracker.Observe(255).Should().BeTrue();
        tracker.Snapshot.State.Should().Be(SpbSequenceState.InOrder);
        tracker.Snapshot.MissingSequences.Should().BeEmpty();
    }

    [Test]
    public void Observe_WraparoundHoleOrderStartsAtExpectedSequence()
    {
        var tracker = new SpbSequenceTracker(TimeProvider.System, 5000);
        tracker.Reset(254);

        tracker.Observe(1).Should().BeTrue();

        tracker.Snapshot.ExpectedSequence.Should().Be(255);
        tracker.Snapshot.MissingSequences.Should().Equal(255, 0);
    }

    [Test]
    public void Observe_ExistingHoleIsResolvedBeforeClassifyingBackwardDelta()
    {
        var tracker = new SpbSequenceTracker(TimeProvider.System, 5000);
        tracker.Reset(254);
        tracker.Observe(1).Should().BeTrue();

        tracker.Observe(255).Should().BeTrue();

        tracker.Snapshot.State.Should().Be(SpbSequenceState.Reordering);
        tracker.Snapshot.ExpectedSequence.Should().Be(0);
        tracker.Snapshot.MissingSequences.Should().Equal(0);
    }

    [Test]
    public void Observe_ForwardJump_TracksEveryMissingSequenceUntilTimeout()
    {
        var tracker = new SpbSequenceTracker(TimeProvider.System, 0);
        tracker.Reset(1);

        tracker.Observe(5).Should().BeTrue();
        tracker.Snapshot.State.Should().Be(SpbSequenceState.Reordering);
        tracker.Snapshot.MissingSequences.Should().Equal(2, 3, 4);
        tracker.CheckTimeout().Should().BeTrue();
        tracker.Snapshot.State.Should().Be(SpbSequenceState.GapDetected);
        tracker.Snapshot.MissingSequences.Should().Equal(2, 3, 4);
    }

    [Test]
    public void Observe_ExpiredGapRemainsLatchedAfterLateFillsAndTraffic()
    {
        var tracker = new SpbSequenceTracker(TimeProvider.System, 0);
        tracker.Reset(1);
        tracker.Observe(4).Should().BeTrue();
        tracker.CheckTimeout().Should().BeTrue();

        tracker.Observe(2).Should().BeTrue();
        tracker.Snapshot.State.Should().Be(SpbSequenceState.GapDetected);
        tracker.Observe(3).Should().BeTrue();
        tracker.Snapshot.State.Should().Be(SpbSequenceState.GapDetected);
        tracker.Snapshot.MissingSequences.Should().BeEmpty();
        tracker.Observe(5).Should().BeTrue();

        tracker.Snapshot.State.Should().Be(SpbSequenceState.GapDetected);
    }

    [TestCase(null)]
    [TestCase(256UL)]
    public void Observe_MissingOrOversizedSequence_IsInvalid(ulong? sequence)
    {
        var tracker = new SpbSequenceTracker(TimeProvider.System, 5000);

        tracker.Observe(sequence).Should().BeFalse();
        tracker.Snapshot.State.Should().Be(SpbSequenceState.Invalid);
    }

    [Test]
    public void Observe_HalfRangeJump_IsInvalid()
    {
        var tracker = new SpbSequenceTracker(TimeProvider.System, 5000);
        tracker.Reset(10);

        tracker.Observe(138).Should().BeFalse();
        tracker.Snapshot.State.Should().Be(SpbSequenceState.Invalid);
    }
}
