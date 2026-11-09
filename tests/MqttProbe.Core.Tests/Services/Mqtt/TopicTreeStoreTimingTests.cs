using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using MQTTnet.Protocol;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Mqtt;

namespace MqttProbe.Core.Tests.Services.Mqtt;

[TestFixture]
public sealed class TopicTreeStoreTimingTests
{
    [Test]
    public void FirstMessageHasZeroElapsedAndNoDeltaPredecessors()
    {
        var store = CreateStore();
        store.Add("a", Message("a", 100));

        var first = Snapshot(store, "a").Should().ContainSingle().Which;

        first.ElapsedSinceBufferStart.Should().Be(TimeSpan.Zero);
        first.SincePreviousAllTopics.Should().BeNull();
        first.SincePreviousSameTopic.Should().BeNull();
    }

    [Test]
    public void ElapsedIsMeasuredFromTheFirstAdmittedMessage()
    {
        var store = CreateStore();
        var first = Message("a", 100);
        var second = Message("a", 600);
        var third = Message("a", 1100);
        store.Add("a", first);
        store.Add("a", second);
        store.Add("a", third);

        first.ElapsedSinceBufferStart.Should().Be(TimeSpan.Zero);
        second.ElapsedSinceBufferStart.Should().Be(TimeSpan.FromMilliseconds(500));
        third.ElapsedSinceBufferStart.Should().Be(TimeSpan.FromSeconds(1));

        second.SincePreviousAllTopics.Should().Be(TimeSpan.FromMilliseconds(500));
        third.SincePreviousAllTopics.Should().Be(TimeSpan.FromMilliseconds(500));
        third.SincePreviousSameTopic.Should().Be(TimeSpan.FromMilliseconds(500));
    }

    [Test]
    public void DeltasUseSerializedReceiveOrderAcrossInterleavedTopics()
    {
        var store = CreateStore();
        var a1 = Message("a", 100);
        var b1 = Message("b", 300);
        var a2 = Message("a", 600);
        store.Add("a", a1);
        store.Add("b", b1);
        store.Add("a", a2);

        a1.SincePreviousAllTopics.Should().BeNull();
        b1.SincePreviousAllTopics.Should().Be(TimeSpan.FromMilliseconds(200));
        a2.SincePreviousAllTopics.Should().Be(TimeSpan.FromMilliseconds(300));

        a1.SincePreviousSameTopic.Should().BeNull();
        b1.SincePreviousSameTopic.Should().BeNull();
        a2.SincePreviousSameTopic.Should().Be(TimeSpan.FromMilliseconds(500));
    }

    [Test]
    public void SameTopicDeltaUsesTheExactRawTopicAndRemainsCaseSensitive()
    {
        var store = CreateStore();
        var upper = Message("Root/A", 100);
        var lower = Message("root/a", 200);
        var upperAgain = Message("Root/A", 300);
        var alias = Message("/Root/A/", 400);
        var upperNext = Message("Root/A", 500);
        var lowerNext = Message("root/a", 700);
        var aliasNext = Message("/Root/A/", 800);
        store.Add("Root/A", upper);
        store.Add("root/a", lower);
        store.Add("Root/A", upperAgain);
        store.Add("/Root/A/", alias);
        store.Add("Root/A", upperNext);
        store.Add("root/a", lowerNext);
        store.Add("/Root/A/", aliasNext);

        upper.SincePreviousSameTopic.Should().BeNull();
        lower.SincePreviousSameTopic.Should().BeNull("MQTT topics are case-sensitive");
        upperAgain.SincePreviousSameTopic.Should().Be(TimeSpan.FromMilliseconds(200));
        alias.SincePreviousSameTopic.Should().BeNull(
            "a distinct raw spelling is a distinct MQTT topic even when it shares a tree node");
        upperNext.SincePreviousSameTopic.Should().Be(TimeSpan.FromMilliseconds(200));
        lowerNext.SincePreviousSameTopic.Should().Be(TimeSpan.FromMilliseconds(500));
        aliasNext.SincePreviousSameTopic.Should().Be(TimeSpan.FromMilliseconds(400));
    }

    [Test]
    public void ClearResetsTheElapsedReferenceAndBothDeltaHistories()
    {
        var store = CreateStore();
        store.Add("a", Message("a", 100));
        store.Add("b", Message("b", 300));
        store.Add("a", Message("a", 600));

        store.Clear();

        var afterClearB = Message("b", 1000);
        var afterClearA = Message("a", 1600);
        store.Add("b", afterClearB);
        store.Add("a", afterClearA);

        afterClearB.ElapsedSinceBufferStart.Should().Be(TimeSpan.Zero);
        afterClearB.SincePreviousAllTopics.Should().BeNull();
        afterClearB.SincePreviousSameTopic.Should().BeNull();

        afterClearA.ElapsedSinceBufferStart.Should().Be(TimeSpan.FromMilliseconds(600));
        afterClearA.SincePreviousAllTopics.Should().Be(TimeSpan.FromMilliseconds(600));
        afterClearA.SincePreviousSameTopic.Should().BeNull("a's predecessor history was cleared");
    }

    [Test]
    public void EvictionKeepsTheReferenceAndPredecessorHistory()
    {
        var store = CreateStore(maxMessages: 2);
        var first = Message("a", 100);
        var second = Message("a", 300);
        var third = Message("a", 900);
        store.Add("a", first);
        store.Add("a", second);
        store.Add("a", third);

        var remaining = Snapshot(store, "a");

        remaining.Should().HaveCount(2);
        second.ElapsedSinceBufferStart.Should().Be(TimeSpan.FromMilliseconds(200));
        third.ElapsedSinceBufferStart.Should().Be(TimeSpan.FromMilliseconds(800),
            "the evicted first message is still the buffer reference");
        third.SincePreviousAllTopics.Should().Be(TimeSpan.FromMilliseconds(600));
        third.SincePreviousSameTopic.Should().Be(TimeSpan.FromMilliseconds(600));
    }

    [Test]
    public void EvictionKeepsTimestampsButDoesNotRetainEvictedMessages()
    {
        var store = CreateStore(maxMessages: 1);
        var evicted = AddAndLoseReference(store, "a", 100);
        store.Add("b", Message("b", 200));

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        evicted.IsAlive.Should().BeFalse(
            "timing history must retain the receive instant, not the message");

        var nextA = Message("a", 900);
        store.Add("a", nextA);

        nextA.ElapsedSinceBufferStart.Should().Be(TimeSpan.FromMilliseconds(800));
        nextA.SincePreviousSameTopic.Should().Be(TimeSpan.FromMilliseconds(800),
            "the evicted same-topic predecessor keeps contributing its timestamp");
    }

    [Test]
    public void ClearingEveryMessageThroughBranchesResetsTimingHistory()
    {
        var store = CreateStore();
        store.Add("a", Message("a", 100));
        store.Add("b", Message("b", 200));
        store.Add("a", Message("a", 600));

        var removed = store.ClearMessagesForBranches(
        [
            new TopicBranchTarget(TopicBranchKind.Raw, "a"),
            new TopicBranchTarget(TopicBranchKind.Raw, "b")
        ]);

        removed.Should().Be(3);
        store.TotalStoredMessages.Should().Be(0);

        var first = Message("b", 1000);
        var second = Message("a", 1600);
        store.Add("b", first);
        store.Add("a", second);

        first.ElapsedSinceBufferStart.Should().Be(TimeSpan.Zero);
        first.SincePreviousAllTopics.Should().BeNull();
        first.SincePreviousSameTopic.Should().BeNull();
        second.ElapsedSinceBufferStart.Should().Be(TimeSpan.FromMilliseconds(600));
        second.SincePreviousAllTopics.Should().Be(TimeSpan.FromMilliseconds(600));
        second.SincePreviousSameTopic.Should().BeNull("the cleared topic history must restart");
    }

    [Test]
    public void ClearingEveryMessageThroughExactTopicsResetsTimingHistory()
    {
        var store = CreateStore();
        store.Add("a", Message("a", 100));
        store.Add("b", Message("b", 200));

        store.ClearMessagesForTopics(["a", "b"]).Should().Be(2);

        var next = Message("a", 500);
        store.Add("a", next);

        next.ElapsedSinceBufferStart.Should().Be(TimeSpan.Zero);
        next.SincePreviousAllTopics.Should().BeNull();
        next.SincePreviousSameTopic.Should().BeNull();
    }

    [Test]
    public void PartialBranchClearPreservesElapsedAndPredecessorHistory()
    {
        var store = CreateStore();
        var first = Message("a", 100);
        var removed = Message("b", 200);
        var second = Message("a", 600);
        store.Add("a", first);
        store.Add("b", removed);
        store.Add("a", second);

        store.ClearMessagesForBranches([new TopicBranchTarget(TopicBranchKind.Raw, "b")])
            .Should().Be(1);

        second.ElapsedSinceBufferStart.Should().Be(TimeSpan.FromMilliseconds(500));
        second.SincePreviousAllTopics.Should().Be(TimeSpan.FromMilliseconds(400));
        second.SincePreviousSameTopic.Should().Be(TimeSpan.FromMilliseconds(500));

        var next = Message("a", 900);
        store.Add("a", next);

        next.ElapsedSinceBufferStart.Should().Be(TimeSpan.FromMilliseconds(800));
        next.SincePreviousAllTopics.Should().Be(TimeSpan.FromMilliseconds(300));
        next.SincePreviousSameTopic.Should().Be(TimeSpan.FromMilliseconds(300));
    }

    [Test]
    public void ClearingAnAlreadyEmptyBufferDoesNotResetTimingHistory()
    {
        var store = CreateStore(maxMessages: 0);
        store.Add("a", Message("a", 100));
        store.TotalStoredMessages.Should().Be(0);

        store.ClearMessagesForBranches([new TopicBranchTarget(TopicBranchKind.Raw, "a")])
            .Should().Be(0);

        var next = Message("a", 300);
        store.Add("a", next);

        next.ElapsedSinceBufferStart.Should().Be(TimeSpan.FromMilliseconds(200));
        next.SincePreviousAllTopics.Should().Be(TimeSpan.FromMilliseconds(200));
        next.SincePreviousSameTopic.Should().Be(TimeSpan.FromMilliseconds(200));
    }

    [Test]
    public void ExcludePurgeDoesNotResetTimingHistory()
    {
        var store = CreateStore();
        store.Add("a", Message("a", 100));
        store.Add("b", Message("b", 200));
        store.Add("a", Message("a", 600));

        store.RemoveMatchingTopic("a");

        var afterPurge = Message("a", 900);
        store.Add("a", afterPurge);

        afterPurge.ElapsedSinceBufferStart.Should().Be(TimeSpan.FromMilliseconds(800));
        afterPurge.SincePreviousAllTopics.Should().Be(TimeSpan.FromMilliseconds(300),
            "the purged predecessor still carries its receive instant");
        afterPurge.SincePreviousSameTopic.Should().Be(TimeSpan.FromMilliseconds(300));
    }

    [Test]
    public void SelectionSortingAndFilteringLeaveStoredTimingsUnchanged()
    {
        var store = CreateStore();
        var a1 = Message("a", 100, "a1");
        var b1 = Message("b", 200, "b1");
        var a2 = Message("a", 300, "a2");
        store.Add("a", a1);
        store.Add("b", b1);
        store.Add("a", a2);

        Snapshot(store, "a").Select(message => message.Payload).Should().Equal("a2", "a1");
        Snapshot(store, "b").Select(message => message.Payload).Should().Equal("b1");

        a2.ElapsedSinceBufferStart.Should().Be(TimeSpan.FromMilliseconds(200));
        a2.SincePreviousAllTopics.Should().Be(TimeSpan.FromMilliseconds(100));
        a2.SincePreviousSameTopic.Should().Be(TimeSpan.FromMilliseconds(200));
        b1.SincePreviousAllTopics.Should().Be(TimeSpan.FromMilliseconds(100));
        b1.SincePreviousSameTopic.Should().BeNull();
    }

    private static MqttMessage[] Snapshot(TopicTreeStore store, string topic)
    {
        store.SelectTopic(topic);
        return store.GetSelectedMessages(store.GetSelectedTopicState().Token, 100)!.Messages.ToArray();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AddAndLoseReference(TopicTreeStore store, string topic, int milliseconds)
    {
        var message = Message(topic, milliseconds);
        store.Add(topic, message);
        return new WeakReference(message);
    }

    private static TopicTreeStore CreateStore(int maxMessages = 100)
    {
        var settings = Substitute.For<IPerformanceSettings>();
        settings.Performance.Returns(new PerformanceSettings
        {
            MaxStoredMessages = maxMessages,
            MaxTopicNodes = 100
        });
        return new TopicTreeStore(settings, Substitute.For<ILogger>());
    }

    private static MqttMessage Message(string topic, int milliseconds, string? payload = null) => new()
    {
        Payload = payload ?? $"{topic}@{milliseconds}",
        Topic = topic,
        DateTimeReceived = DateTime.UnixEpoch.AddMilliseconds(milliseconds),
        QualityOfServiceLevel = MqttQualityOfServiceLevel.AtMostOnce
    };
}
