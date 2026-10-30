using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Mqtt;

namespace MqttProbe.Core.Tests.Services.Mqtt;

[TestFixture]
public sealed class TopicTreeStoreScopedClearTests
{
    private const string NodeData = "spBv1.0/plant1/NDATA/node1";
    private const string NodeBirth = "spBv1.0/plant1/NBIRTH/node1";
    private const string Unrelated = "sensors/temp";

    [Test]
    public void Clear_RemovesOnlyTheNamedTopics()
    {
        var store = CreateStore();
        AddCanonical(store, NodeData, "node-data-1", 1);
        AddCanonical(store, NodeData, "node-data-2", 2);
        AddCanonical(store, Unrelated, "unrelated", 3);

        var removed = store.ClearMessagesForTopics([NodeData]);

        removed.Should().Be(2);
        store.TotalStoredMessages.Should().Be(1);
        Payloads(store, Unrelated).Should().Equal("unrelated");
        Payloads(store, NodeData).Should().BeEmpty();
    }

    [Test]
    public void Clear_DoesNotWidenToDescendants()
    {
        var store = CreateStore();
        AddCanonical(store, "plant/area", "area", 1);
        AddCanonical(store, "plant/area/temp", "temp", 2);

        var removed = store.ClearMessagesForTopics(["plant/area"]);

        removed.Should().Be(1);
        Payloads(store, "plant/area/temp").Should().Equal("temp");
    }

    [Test]
    public void Clear_KeepsTheTreeRowAndItsObservedMarkerAtZero()
    {
        var store = CreateStore();
        AddCanonical(store, NodeData, "node-data", 1);

        store.ClearMessagesForTopics([NodeData]);

        var row = FindRow(store, NodeData);
        row.Should().NotBeNull();
        row.MessageCount.Should().Be(0);
        row.HasDirectMessages.Should().BeFalse();
        row.HasObservedDirectTopic.Should().BeTrue();
    }

    [Test]
    public void Clear_UpdatesTheSelectedCountAndContentVersionWithoutMovingTheSelection()
    {
        var store = CreateStore();
        AddCanonical(store, NodeData, "node-data", 1);
        store.SelectTopics(TopicSelection.ForExactTopics([NodeData]));
        var before = store.GetSelectedTopicState();

        store.ClearMessagesForTopics(before.Paths);

        var after = store.GetSelectedTopicState();
        after.Token.Generation.Should().Be(before.Token.Generation);
        after.Token.ContentVersion.Should().BeGreaterThan(before.Token.ContentVersion);
        after.MessageCount.Should().Be(0);
        after.Paths.Should().Equal(before.Paths);
        store.GetSelectedMessages(after.Token, 10)!.Messages.Should().BeEmpty();
    }

    [Test]
    public void Clear_LeavesMessagesArrivingAfterwardsUntouched()
    {
        var store = CreateStore();
        AddCanonical(store, NodeData, "node-data", 1);
        store.SelectTopics(TopicSelection.ForExactTopics([NodeData]));

        store.ClearMessagesForTopics([NodeData]);
        AddCanonical(store, NodeData, "future", 2);

        var state = store.GetSelectedTopicState();
        state.MessageCount.Should().Be(1);
        Payloads(store, NodeData).Should().Equal("future");
    }

    [Test]
    public void Clear_KeepsRetentionOrderConsistentForTheRemainingMessages()
    {
        var store = CreateStore(maxMessages: 2);
        AddCanonical(store, NodeData, "node-data", 1);
        AddCanonical(store, Unrelated, "unrelated-older", 2);

        store.ClearMessagesForTopics([NodeData]);
        AddCanonical(store, NodeBirth, "birth", 3);
        AddCanonical(store, "sensors/humidity", "humidity", 4);

        store.TotalStoredMessages.Should().Be(2);
        Payloads(store, Unrelated).Should().BeEmpty("the oldest surviving message is evicted, not a cleared one");
        Payloads(store, NodeBirth).Should().Equal("birth");
        Payloads(store, "sensors/humidity").Should().Equal("humidity");
    }

    [Test]
    public void Clear_IgnoresUnknownAndDuplicatePaths()
    {
        var store = CreateStore();
        AddCanonical(store, NodeData, "node-data", 1);

        var removed = store.ClearMessagesForTopics([NodeData, NodeData, "missing/topic", "  "]);

        removed.Should().Be(1);
        store.TotalStoredMessages.Should().Be(0);
    }

    [Test]
    public void Clear_RemovesOnlyTheNamedRawPath()
    {
        var store = CreateStore();
        AddCanonical(store, "a/b", "canonical", 1);
        AddCanonical(store, "a//b", "slashed", 2);

        var removed = store.ClearMessagesForTopics(["a/b"]);

        removed.Should().Be(1);
        store.TotalStoredMessages.Should().Be(1);
        Payloads(store, "a//b").Should().Equal("slashed");
        Payloads(store, "a/b").Should().BeEmpty();
    }

    [Test]
    public void Clear_KeepsZeroCountSelectionAndLaterArrivalsExact()
    {
        var store = CreateStore();
        AddCanonical(store, "a/b", "canonical", 1);
        AddCanonical(store, "a//b", "slashed", 2);
        store.SelectTopics(TopicSelection.ForExactTopics(["a/b"]));
        var selected = store.GetSelectedTopicState();

        store.ClearMessagesForTopics(selected.Paths);

        store.GetSelectedTopicState().MessageCount.Should().Be(0);
        store.SelectTopics(TopicSelection.ForExactTopics(["a/b"]));
        selected = store.GetSelectedTopicState();

        AddCanonical(store, "a//b", "slashed-later", 3);
        Payloads(store, selected).Should().BeEmpty("the sibling spelling is not the selected topic");

        AddCanonical(store, "a/b", "canonical-later", 4);
        Payloads(store, selected).Should().Equal("canonical-later");
    }

    [Test]
    public void Clear_KeepsExistingRetentionOrderInsteadOfSortingByTimestamp()
    {
        var store = CreateStore(maxMessages: 3);
        AddCanonical(store, "keep/a", "a", 100);
        AddCanonical(store, "keep/b", "b", 1);
        AddCanonical(store, "cleared/c", "c", 50);

        store.ClearMessagesForTopics(["cleared/c"]);
        AddCanonical(store, "keep/d", "d", 0);
        AddCanonical(store, "keep/e", "e", -5);

        store.TotalStoredMessages.Should().Be(3);
        Payloads(store, "keep/a").Should().BeEmpty("arrival order decides eviction, not timestamp");
        Payloads(store, "keep/b").Should().Equal("b");
        Payloads(store, "keep/d").Should().Equal("d");
        Payloads(store, "keep/e").Should().Equal("e");
    }

    [Test]
    public void Clear_KeepsArrivalOrderForEqualTimestamps()
    {
        var store = CreateStore(maxMessages: 3);
        AddCanonical(store, "keep/a", "a", 5);
        AddCanonical(store, "keep/b", "b", 5);
        AddCanonical(store, "cleared/c", "c", 5);

        store.ClearMessagesForTopics(["cleared/c"]);
        AddCanonical(store, "keep/d", "d", 5);
        AddCanonical(store, "keep/e", "e", 5);

        Payloads(store, "keep/a").Should().BeEmpty("ties retain arrival order");
        Payloads(store, "keep/b").Should().Equal("b");
        Payloads(store, "keep/d").Should().Equal("d");
        Payloads(store, "keep/e").Should().Equal("e");
    }

    [Test]
    public void Clear_CountsRepeatedReferencesPerStoredOccurrence()
    {
        var store = CreateStore(maxMessages: 3);
        var repeated = Message("same", 1, "a/b");
        store.Add("a/b", repeated);
        store.Add("a/b", repeated);
        AddCanonical(store, "keep/b", "b", 2);

        var removed = store.ClearMessagesForTopics(["a/b"]);

        removed.Should().Be(2);
        store.TotalStoredMessages.Should().Be(1);

        AddCanonical(store, "keep/c", "c", 3);
        AddCanonical(store, "keep/d", "d", 4);

        store.TotalStoredMessages.Should().Be(3);
        Payloads(store, "keep/b").Should().Equal("b");
        Payloads(store, "keep/c").Should().Equal("c");
        Payloads(store, "keep/d").Should().Equal("d");
    }

    [Test]
    public void Clear_KeepsTheSameReferenceStoredUnderAnotherNode()
    {
        var store = CreateStore(maxMessages: 2);
        var shared = Message("shared", 1);
        store.Add("a", shared);
        store.Add("b", shared);

        store.ClearMessagesForTopics(["a"]);

        store.TotalStoredMessages.Should().Be(1);
        Payloads(store, "b").Should().Equal("shared");

        AddCanonical(store, "c", "c", 2);
        AddCanonical(store, "d", "d", 3);

        Payloads(store, "b").Should().BeEmpty("the surviving occurrence is the oldest entry");
        Payloads(store, "c").Should().Equal("c");
        Payloads(store, "d").Should().Equal("d");
    }

    private static IEnumerable<string?> Payloads(TopicTreeStore store, string topic)
    {
        store.SelectTopics(TopicSelection.ForExactTopics([topic]));
        var state = store.GetSelectedTopicState();
        return store.GetSelectedMessages(state.Token, 100)!.Messages.Select(message => message.Payload);
    }

    private static IEnumerable<string?> Payloads(TopicTreeStore store, SelectedTopicState state) =>
        store.GetSelectedMessages(state.Token, 100)!.Messages.Select(message => message.Payload);

    private static TopicNodeSnapshot? FindRow(TopicTreeStore store, string fullTopic)
    {
        static TopicNodeSnapshot? Walk(ImmutableArray<TopicNodeSnapshot> nodes, string path)
        {
            foreach (var node in nodes)
            {
                if (string.Equals(node.FullTopic, path, StringComparison.Ordinal))
                    return node;

                if (Walk(node.Children, path) is { } match)
                    return match;
            }

            return null;
        }

        return Walk(store.GetTopicTreeSnapshot().Roots, fullTopic);
    }

    private static void AddCanonical(TopicTreeStore store, string topic, string payload, int second) =>
        store.Add(topic, Message(payload, second, topic));

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

    private static MqttMessage Message(string payload, int second, string? topic = null) => new()
    {
        Payload = payload,
        Topic = topic,
        DateTimeReceived = DateTime.UnixEpoch.AddSeconds(second),
        QualityOfServiceLevel = MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce
    };
}
