using Microsoft.Extensions.Logging;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Mqtt;

namespace MqttProbe.Core.Tests.Services.Mqtt;

[TestFixture]
public sealed class MessageStoreReadContractTests
{
    [Test]
    public void TreeSnapshot_IsCachedUntilTreeOrMessageContentChanges_AndRemainsDetached()
    {
        var store = CreateStore();
        store.Add("root/child", Message("first", 1));

        var first = store.GetTopicTreeSnapshot();
        var repeated = store.GetTopicTreeSnapshot();

        repeated.Should().BeSameAs(first);
        first.Roots.Should().ContainSingle();
        first.Roots[0].Children.Should().ContainSingle();
        first.Roots[0].Children[0].HasDirectMessages.Should().BeTrue();

        store.Add("root/child", Message("second", 2));
        var updated = store.GetTopicTreeSnapshot();

        updated.Should().NotBeSameAs(first);
        updated.Version.Should().BeGreaterThan(first.Version);
        first.Roots[0].MessageCount.Should().Be(1);
        updated.Roots[0].MessageCount.Should().Be(2);
    }

    [Test]
    public void SelectTopic_NormalizesSlashesButPreservesCaseAndWhitespace()
    {
        var store = CreateStore();
        store.Add("Root// Child /Leaf/", Message("x", 1));

        var result = store.SelectTopic("/Root// Child /Leaf/");

        result.Status.Should().Be(TopicSelectionStatus.Selected);
        result.State.FullTopic.Should().Be("Root/ Child /Leaf");
        result.State.MessageCount.Should().Be(1);
    }

    [Test]
    public void InvalidAndMissingSelectionPreserveTheCurrentSelection()
    {
        var store = CreateStore();
        store.Add("root/child", Message("x", 1));
        store.SelectTopic("root/child");
        var selected = store.GetSelectedTopicState();

        var empty = store.SelectTopic(string.Empty);
        var slashes = store.SelectTopic("///");
        var missing = store.SelectTopic("root/missing");

        empty.Status.Should().Be(TopicSelectionStatus.InvalidPath);
        slashes.Status.Should().Be(TopicSelectionStatus.InvalidPath);
        missing.Status.Should().Be(TopicSelectionStatus.NotFound);
        empty.State.Should().Be(selected);
        slashes.State.Should().Be(selected);
        missing.State.Should().Be(selected);
    }

    [Test]
    public void SelectionGenerationRejectsForeignAndAbaTokensButIgnoresContentVersion()
    {
        var store = CreateStore();
        var otherStore = CreateStore();
        store.Add("root/a", Message("a", 1));
        store.Add("root/b", Message("b", 2));
        otherStore.Add("root/a", Message("foreign", 3));

        var a = store.SelectTopic("root/a").State;
        var foreign = otherStore.SelectTopic("root/a").State;
        store.SelectTopic("root/b");
        var aAgain = store.SelectTopic("root/a").State;

        aAgain.Token.Generation.Should().BeGreaterThan(a.Token.Generation);
        store.GetSelectedMessages(a.Token, 10).Should().BeNull();
        store.GetSelectedMessages(foreign.Token, 10).Should().BeNull();

        store.Add("root/a", Message("new", 4));
        var contentAdvanced = store.GetSelectedMessages(aAgain.Token, 10);

        contentAdvanced.Should().NotBeNull();
        contentAdvanced!.State.Token.ContentVersion.Should().BeGreaterThan(aAgain.Token.ContentVersion);
        contentAdvanced.Messages.Should().Contain(m => m.Payload == "new");
    }

    [Test]
    public void SelectedMessagesAreNewestFirstBoundedDetachedAndRetainOriginalReferences()
    {
        var store = CreateStore();
        var oldest = Message("old", 1);
        var middle = Message("middle", 2);
        var newest = Message("new", 3);
        store.Add("root", oldest);
        store.Add("root/child", middle);
        store.Add("root/child", newest);
        var state = store.SelectTopic("root").State;

        var selected = store.GetSelectedMessages(state.Token, 2);

        selected.Should().NotBeNull();
        selected!.Messages.Should().Equal(newest, middle);
        selected.Messages[0].Should().BeSameAs(newest);
        selected.Limit.Should().Be(2);
        store.GetSelectedMessages(state.Token, 0)!.Messages.Should().BeEmpty();
        Assert.Throws<ArgumentOutOfRangeException>(() => store.GetSelectedMessages(state.Token, -1));
    }

    [Test]
    public void ClearAndPurgeInvalidateSelectionAndRecreatedPathGetsANewGeneration()
    {
        var store = CreateStore();
        store.Add("root/child", Message("before", 1));
        var initial = store.SelectTopic("root/child").State;

        store.Clear();
        store.GetSelectedTopicState().FullTopic.Should().BeNull();
        store.GetSelectedMessages(initial.Token, 10).Should().BeNull();

        store.Add("root/child", Message("after-clear", 2));
        var recreated = store.SelectTopic("root/child").State;
        recreated.Token.Generation.Should().BeGreaterThan(initial.Token.Generation);

        store.RemoveMatchingTopic("#");
        store.GetSelectedTopicState().FullTopic.Should().BeNull();
        store.GetSelectedMessages(recreated.Token, 10).Should().BeNull();

        store.Add("root/child", Message("after-purge", 3));
        var afterPurge = store.SelectTopic("root/child").State;
        afterPurge.Token.Generation.Should().BeGreaterThan(recreated.Token.Generation);
    }

    [Test]
    public void ClearAlwaysInvalidatesButRepeatedSelectionAndNullClearAreNoOps()
    {
        var store = CreateStore();
        var empty = store.GetSelectedTopicState();
        store.SelectTopic(null).State.Token.Should().Be(empty.Token);

        store.Add("root", Message("x", 1));
        var selected = store.SelectTopic("root").State;
        store.SelectTopic("root").State.Token.Should().Be(selected.Token);
        store.SelectTopic(null).State.Token.Generation.Should().Be(selected.Token.Generation + 1);

        var cleared = store.GetSelectedTopicState();
        store.Clear();
        store.GetSelectedTopicState().Token.Generation.Should().Be(cleared.Token.Generation + 1);
    }

    [Test]
    public void RootCountIsConstantTimeAndRetentionLeavesTopicNodesInPlace()
    {
        var store = CreateStore(maxMessages: 1);
        store.Add("one/child", Message("one", 1));
        store.Add("two/child", Message("two", 2));

        store.RootTopicCount.Should().Be(2);
        store.TotalStoredMessages.Should().Be(1);
        var tree = store.GetTopicTreeSnapshot();
        tree.Roots.Should().HaveCount(2);
        tree.Roots.Should().Contain(node => node.FullTopic == "one" && node.MessageCount == 0);
        tree.Roots.Should().Contain(node => node.FullTopic == "two" && node.MessageCount == 1);
    }

    [Test]
    public void NodeLimitPartialCreationInvalidatesSnapshotAndAdvancesTreeVersion()
    {
        var store = CreateStore(maxTopicNodes: 2);
        var before = store.GetTopicTreeSnapshot();

        store.Add("root/child/leaf", Message("dropped", 1));
        var after = store.GetTopicTreeSnapshot();

        after.Should().NotBeSameAs(before);
        after.Version.Should().BeGreaterThan(before.Version);
        after.Roots.Should().ContainSingle(root => root.FullTopic == "root");
        after.Roots[0].Children.Should().ContainSingle(child => child.FullTopic == "root/child");
        after.Roots[0].MessageCount.Should().Be(0);
        store.TotalStoredMessages.Should().Be(0);
    }

    [Test]
    public void PartialNodeCreationAdvancesSelectedContentVersionWithoutChangingGeneration()
    {
        var store = CreateStore(maxTopicNodes: 2);
        store.Add("root", Message("stored", 1, "root"));
        var selected = store.SelectTopic("root").State;

        store.Add("root/child/leaf", Message("rejected", 2, "root/child/leaf"));

        var after = store.GetSelectedTopicState();
        after.Token.Generation.Should().Be(selected.Token.Generation);
        after.Token.ContentVersion.Should().BeGreaterThan(selected.Token.ContentVersion);
        store.GetTopicTreeSnapshot().Roots.Single().Children.Should().ContainSingle();
    }

    [Test]
    public void SelectedSnapshotMembershipRemainsDetachedAfterRetentionPurgeAndClear()
    {
        var store = CreateStore(maxMessages: 2);
        var first = Message("first", 1, "root/a");
        var second = Message("second", 2, "root/b");
        store.Add("root/a", first);
        store.Add("root/b", second);
        var selected = store.SelectTopic("root").State;
        var beforeRetention = store.GetSelectedMessages(selected.Token, 10)!;

        var third = Message("third", 3, "root/c");
        store.Add("root/c", third);
        beforeRetention.Messages.Should().Equal(second, first);
        store.GetSelectedMessages(selected.Token, 10)!.Messages.Should().Equal(third, second);

        store.RemoveMatchingTopic("#");
        beforeRetention.Messages.Should().Equal(second, first);
        beforeRetention.State.MessageCount.Should().Be(2);
        store.GetSelectedMessages(selected.Token, 10).Should().BeNull();

        store.Add("root", Message("after-purge", 4, "root"));
        var afterPurge = store.SelectTopic("root").State;
        var beforeClear = store.GetSelectedMessages(afterPurge.Token, 10)!;
        store.Clear();
        beforeClear.Messages.Should().ContainSingle().Which.Payload.Should().Be("after-purge");
        store.GetSelectedMessages(afterPurge.Token, 10).Should().BeNull();
    }

    [Test]
    public void TreeSnapshotCacheInvalidatesAfterRetentionPurgeAndClear()
    {
        var store = CreateStore(maxMessages: 1);
        store.Add("root", Message("first", 1, "root"));
        var beforeRetention = store.GetTopicTreeSnapshot();

        store.Add("root", Message("second", 2, "root"));
        var afterRetention = store.GetTopicTreeSnapshot();

        store.GetTopicTreeSnapshot().Should().BeSameAs(afterRetention);
        afterRetention.Should().NotBeSameAs(beforeRetention);
        afterRetention.Version.Should().BeGreaterThan(beforeRetention.Version);
        beforeRetention.Roots.Single().MessageCount.Should().Be(1);
        afterRetention.Roots.Single().MessageCount.Should().Be(1);

        store.RemoveMatchingTopic("#");
        var afterPurge = store.GetTopicTreeSnapshot();
        afterPurge.Should().NotBeSameAs(afterRetention);
        afterPurge.Version.Should().BeGreaterThan(afterRetention.Version);
        afterPurge.Roots.Should().BeEmpty();

        store.Add("root", Message("after-purge", 3, "root"));
        var beforeClear = store.GetTopicTreeSnapshot();
        store.Clear();
        var afterClear = store.GetTopicTreeSnapshot();
        afterClear.Should().NotBeSameAs(beforeClear);
        afterClear.Version.Should().BeGreaterThan(beforeClear.Version);
        afterClear.Roots.Should().BeEmpty();
    }

    [Test]
    public void SelectionContentVersionAdvancesOnRetentionAndPartialPurge()
    {
        var store = CreateStore(maxMessages: 1);
        store.Add("root/first", Message("first", 1, "root/first"));
        var selected = store.SelectTopic("root").State;

        store.Add("root/second", Message("second", 2, "root/second"));
        var afterRetention = store.GetSelectedTopicState();
        afterRetention.Token.Generation.Should().Be(selected.Token.Generation);
        afterRetention.Token.ContentVersion.Should().BeGreaterThan(selected.Token.ContentVersion);
        store.GetSelectedMessages(selected.Token, 10)!.Messages
            .Should().ContainSingle().Which.Payload.Should().Be("second");

        var partialStore = CreateStore();
        partialStore.Add("root/keep", Message("keep", 1, "root/keep"));
        partialStore.Add("root/remove", Message("remove", 2, "root/remove"));
        var partialSelection = partialStore.SelectTopic("root").State;
        partialStore.RemoveMatchingTopic("root/remove");
        var afterPartialPurge = partialStore.GetSelectedTopicState();
        afterPartialPurge.FullTopic.Should().Be("root");
        afterPartialPurge.Token.Generation.Should().Be(partialSelection.Token.Generation);
        afterPartialPurge.Token.ContentVersion.Should().BeGreaterThan(partialSelection.Token.ContentVersion);
        partialStore.GetSelectedMessages(partialSelection.Token, 10)!.Messages
            .Should().ContainSingle().Which.Payload.Should().Be("keep");
    }

    [Test]
    public async Task ConcurrentWriterAndReaderCaptureMatchingRootCountAndMessages()
    {
        const int iterations = 50;
        var store = CreateStore(maxMessages: iterations + 1);
        store.Add("root", Message("initial", 0, "root"));
        var token = store.SelectTopic("root").State.Token;
        using var barrier = new Barrier(2);
        var snapshots = new List<SelectedMessagesSnapshot>(iterations);

        var writer = Task.Run(() =>
        {
            for (var i = 1; i <= iterations; i++)
            {
                if (!barrier.SignalAndWait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Writer could not synchronize with the reader.");
                store.Add("root", Message($"message-{i}", i, "root"));
            }
        });
        var reader = Task.Run(() =>
        {
            for (var i = 0; i < iterations; i++)
            {
                if (!barrier.SignalAndWait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Reader could not synchronize with the writer.");
                snapshots.Add(store.GetSelectedMessages(token, iterations + 1)!);
            }
        });

        await Task.WhenAll(writer, reader).WaitAsync(TimeSpan.FromSeconds(15));

        snapshots.Should().HaveCount(iterations);
        snapshots.Should().OnlyContain(snapshot =>
            snapshot.State.MessageCount == snapshot.Messages.Length);
    }

    [Test]
    public async Task QueryRacingClearOrPurgeReturnsOnlyCoherentOldSnapshotOrStaleNull()
    {
        foreach (var purge in new[] { false, true })
        {
            var store = CreateStore();
            var original = Message("before", 1, "root/child");
            store.Add("root/child", original);
            var selected = store.SelectTopic("root/child").State;
            using var barrier = new Barrier(2);

            var query = Task.Run(() =>
            {
                if (!barrier.SignalAndWait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Query could not synchronize with invalidation.");
                return store.GetSelectedMessages(selected.Token, 10);
            });
            var invalidate = Task.Run(() =>
            {
                if (!barrier.SignalAndWait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Invalidation could not synchronize with query.");
                if (purge)
                    store.RemoveMatchingTopic("#");
                else
                    store.Clear();
                store.Add("root/child", Message("after", 2, "root/child"));
                store.SelectTopic("root/child");
            });

            await Task.WhenAll(query, invalidate).WaitAsync(TimeSpan.FromSeconds(15));
            var result = query.Result;
            if (result is null)
                continue;

            result.State.Token.Generation.Should().Be(selected.Token.Generation);
            result.State.FullTopic.Should().Be("root/child");
            result.State.MessageCount.Should().Be(1);
            result.Messages.Should().ContainSingle().Which.Should().BeSameAs(original);
        }
    }

    private static TopicTreeStore CreateStore(int maxMessages = 100, int maxTopicNodes = 100)
    {
        var configuration = new AppConfiguration
        {
            Performance = new PerformanceSettings
            {
                MaxStoredMessages = maxMessages,
                MaxTopicNodes = maxTopicNodes
            }
        };
        var settings = Substitute.For<IPerformanceSettings>();
        settings.Performance.Returns(configuration.Performance);
        return new TopicTreeStore(settings, Substitute.For<ILogger>());
    }

    private static MqttMessage Message(string payload, int second, string topic = "topic") => new(payload, topic, false,
        MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce)
    {
        DateTimeReceived = DateTime.UnixEpoch.AddSeconds(second)
    };
}
