using Microsoft.Extensions.Logging;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Mqtt;

namespace MqttProbe.Core.Tests.Services.Mqtt;

[TestFixture]
public sealed class TopicTreeStoreMultiSelectionTests
{
    private const string NodeData = "spBv1.0/plant1/NDATA/node1";
    private const string NodeBirth = "spBv1.0/plant1/NBIRTH/node1";
    private const string DeviceData = "spBv1.0/plant1/DDATA/node1/dev1";

    [Test]
    public void ExactSelection_ExposesSortedExactPathsAndMode()
    {
        var store = CreateStore();
        AddCanonical(store, NodeData, "node-data", 1);
        AddCanonical(store, NodeBirth, "birth", 2);
        AddCanonical(store, "unrelated/topic", "unrelated", 3);

        store.SelectTopics(TopicSelection.ForExactTopics([NodeData, NodeBirth]));

        var state = store.GetSelectedTopicState();
        state.Mode.Should().Be(TopicSelectionMode.MultipleTopics);
        state.Paths.Should().Equal(NodeBirth, NodeData);
        state.MessageCount.Should().Be(2);
        Payloads(store, state).Should().BeEquivalentTo(["node-data", "birth"]);
    }

    [Test]
    public void ExactSelection_UnionsRowsWithoutDuplicatingMessages()
    {
        var store = CreateStore();
        AddCanonical(store, NodeBirth, "birth", 1);
        AddCanonical(store, NodeData, "node-data", 2);
        AddCanonical(store, DeviceData, "dev1", 3);

        store.SelectTopics(TopicSelection.ForExactTopics([NodeData, NodeBirth, NodeData]));

        var state = store.GetSelectedTopicState();
        state.MessageCount.Should().Be(2);
        state.Paths.Should().HaveCount(2);
        Payloads(store, state).Should().BeEquivalentTo(["birth", "node-data"]);
    }

    [Test]
    public void ExactSelection_DoesNotWidenToDescendants()
    {
        var store = CreateStore();
        AddCanonical(store, "plant/area", "area", 1);
        AddCanonical(store, "plant/area/temp", "temp", 2);

        store.SelectTopics(TopicSelection.ForExactTopics(["plant/area"]));

        var state = store.GetSelectedTopicState();
        state.Paths.Should().Equal("plant/area");
        state.MessageCount.Should().Be(1);
        Payloads(store, state).Should().Equal("area");
    }

    [Test]
    public void ExactSelection_AncestorAndDescendantEachContributeOnlyTheirOwnMessages()
    {
        var store = CreateStore();
        AddCanonical(store, "plant/area", "area", 1);
        AddCanonical(store, "plant/area/temp", "temp", 2);
        AddCanonical(store, "plant/area/temp/deep", "deep", 3);

        store.SelectTopics(TopicSelection.ForExactTopics(["plant/area/temp", "plant/area"]));

        var state = store.GetSelectedTopicState();
        state.Paths.Should().Equal("plant/area", "plant/area/temp");
        state.MessageCount.Should().Be(2, "the descendant of a selected parent is not widened in");
        Payloads(store, state).Should().BeEquivalentTo(["area", "temp"]);
    }

    [Test]
    public void ExactSelection_KeepsNewestFirstAndAdmitsLaterArrivals()
    {
        var store = CreateStore();
        AddCanonical(store, NodeData, "older", 1);
        AddCanonical(store, NodeBirth, "newer", 3);
        store.SelectTopics(TopicSelection.ForExactTopics([NodeData, NodeBirth]));

        AddCanonical(store, NodeBirth, "latest", 5);

        var state = store.GetSelectedTopicState();
        Payloads(store, state).Should().Equal("latest", "newer", "older");
        state.MessageCount.Should().Be(3);
    }

    [Test]
    public void ExactSelection_KeepsIdentityAcrossReorderingAndContentChanges()
    {
        var store = CreateStore();
        AddCanonical(store, NodeData, "node-data", 1);
        AddCanonical(store, NodeBirth, "birth", 2);

        var selection = TopicSelection.ForExactTopics([NodeData, NodeBirth]);
        store.SelectTopics(selection);
        var first = store.GetSelectedTopicState();

        AddCanonical(store, NodeBirth, "birth-later", 3);
        store.SelectTopics(TopicSelection.ForExactTopics([NodeBirth, NodeData]));

        var after = store.GetSelectedTopicState();
        after.Token.Generation.Should().Be(first.Token.Generation, "row order is not selection identity");
        after.Token.ContentVersion.Should().BeGreaterThan(first.Token.ContentVersion);
        after.Paths.Should().Equal(first.Paths);
        after.Key.Should().Be(selection.Key);
    }

    [Test]
    public void ExactSelection_IsUnchangedByUnrelatedTreeUpdates()
    {
        var store = CreateStore();
        AddCanonical(store, NodeData, "node-data", 1);
        AddCanonical(store, NodeBirth, "birth", 2);
        store.SelectTopics(TopicSelection.ForExactTopics([NodeData, NodeBirth]));
        var before = store.GetSelectedTopicState();

        AddCanonical(store, "unrelated/topic", "unrelated", 3);
        store.GetTopicTreeSnapshot();
        var after = store.GetSelectedTopicState();

        after.Should().Be(before, "a tree refresh or regroup does not move an exact selection");
    }

    [Test]
    public void ExactSelection_ReportsOnlyThePathsThatResolve()
    {
        var store = CreateStore();
        AddCanonical(store, NodeData, "node-data", 1);
        AddCanonical(store, NodeBirth, "birth", 2);

        var request = TopicSelection.ForExactTopics([NodeData, NodeBirth, "missing/topic"]);
        store.SelectTopics(request);

        var state = store.GetSelectedTopicState();
        state.Mode.Should().Be(TopicSelectionMode.MultipleTopics);
        state.Paths.Should().Equal(NodeBirth, NodeData);
        state.Key.Should().NotBe(request.Key, "the request named a row that no longer resolves");
        Payloads(store, state).Should().BeEquivalentTo(["node-data", "birth"]);
    }

    [Test]
    public void ExactSelection_WithNoResolvablePathClearsInsteadOfKeepingTheStaleSelection()
    {
        var store = CreateStore();
        AddCanonical(store, NodeData, "node-data", 1);
        store.SelectTopics(TopicSelection.ForExactTopics([NodeData]));
        var selected = store.GetSelectedTopicState();

        store.SelectTopics(TopicSelection.ForExactTopics(["missing/topic"]));

        var state = store.GetSelectedTopicState();
        state.Key.Should().BeNull();
        state.Paths.Should().BeEmpty();
        state.MessageCount.Should().Be(0);
        store.GetSelectedMessages(selected.Token, 10).Should().BeNull();
    }

    [Test]
    public void ExactSelection_KeepsTheSurvivingPathsWhenAPurgeRemovesAnotherSelectedTopic()
    {
        var store = CreateStore();
        AddCanonical(store, NodeData, "node-data", 1);
        AddCanonical(store, NodeBirth, "birth", 2);
        store.SelectTopics(TopicSelection.ForExactTopics([NodeData, NodeBirth]));

        store.RemoveMatchingTopic(NodeBirth);

        var state = store.GetSelectedTopicState();
        state.Mode.Should().Be(TopicSelectionMode.MultipleTopics);
        state.Paths.Should().Equal(NodeData);
        state.Key.Should().Be(TopicSelection.ForExactTopics([NodeData]).Key);
        state.MessageCount.Should().Be(1);

        AddCanonical(store, NodeData, "after-purge", 3);
        Payloads(store, state).Should().BeEquivalentTo(["node-data", "after-purge"]);
    }

    [Test]
    public void ExactSelection_KeepsAZeroMessageRowSelectable()
    {
        var store = CreateStore();
        AddCanonical(store, NodeData, "node-data", 1);
        store.SelectTopics(TopicSelection.ForExactTopics([NodeData]));

        store.ClearMessagesForTopics([NodeData]);

        var state = store.GetSelectedTopicState();
        state.Paths.Should().Equal(NodeData);
        state.MessageCount.Should().Be(0);

        AddCanonical(store, NodeData, "future", 2);
        var after = store.GetSelectedTopicState();
        after.Paths.Should().Equal(NodeData);
        Payloads(store, after).Should().Equal("future");
    }

    [Test]
    public void ExactSelection_KeysDoNotCollideOnTopicNamesContainingNewlines()
    {
        var store = CreateStore();
        AddCanonical(store, "a\nb", "a-newline-b", 1);
        AddCanonical(store, "c", "c", 2);
        AddCanonical(store, "a", "a", 3);
        AddCanonical(store, "b\nc", "b-newline-c", 4);

        var first = TopicSelection.ForExactTopics(["a\nb", "c"]);
        var second = TopicSelection.ForExactTopics(["a", "b\nc"]);

        first.Key.Should().NotBe(second.Key, "newlines are valid in MQTT topic names");
        store.SelectTopics(first);
        store.GetSelectedTopicState().Key.Should().Be(first.Key);

        store.SelectTopics(second);

        var state = store.GetSelectedTopicState();
        state.Key.Should().Be(second.Key);
        state.Paths.Should().Equal("a", "b\nc");
        state.MessageCount.Should().Be(2);
    }

    [Test]
    public void ExactSelection_KeepsDistinctRawSpellingsOnTheSameNodeApart()
    {
        var store = CreateStore();
        AddCanonical(store, "a/b", "canonical", 1);
        store.Add("a/b", Message("alias", 2, "a//b"));

        store.SelectTopics(TopicSelection.ForExactTopics(["a/b"]));
        var canonical = store.GetSelectedTopicState();
        canonical.Paths.Should().Equal("a/b");
        Payloads(store, canonical).Should().Equal("canonical");

        store.SelectTopics(TopicSelection.ForExactTopics(["a//b"]));
        var alias = store.GetSelectedTopicState();
        alias.Paths.Should().Equal("a//b");
        Payloads(store, alias).Should().Equal("alias");
    }

    [Test]
    public void ExactSelection_KeepsLeadingAndTrailingSlashSpellingsExact()
    {
        var store = CreateStore();
        AddCanonical(store, "a/b", "bare", 1);
        store.Add("a/b", Message("slashed", 2, "/a/b/"));

        store.SelectTopics(TopicSelection.ForExactTopics(["/a/b/"]));

        var state = store.GetSelectedTopicState();
        state.Paths.Should().Equal("/a/b/");
        Payloads(store, state).Should().Equal("slashed");
    }

    [Test]
    public void ExactSelection_PrunesASelectedSpellingRetiredByExclusionEvenWhenAnAliasKeepsTheNode()
    {
        var store = CreateStore();
        AddCanonical(store, "a/b", "canonical", 1);
        store.Add("a/b", Message("alias", 2, "a//b"));
        store.SelectTopics(TopicSelection.ForExactTopics(["a/b"]));

        store.RemoveMatchingTopic("a/b");

        var state = store.GetSelectedTopicState();
        state.Key.Should().BeNull();
        state.Paths.Should().BeEmpty();
    }

    [Test]
    public void ExactSelection_ExposesEveryObservedSpellingOnTheSnapshotRow()
    {
        var store = CreateStore();
        AddCanonical(store, "a/b", "canonical", 1);
        store.Add("a/b", Message("alias", 2, "a//b"));

        var row = store.GetTopicTreeSnapshot().Roots
            .SelectMany(Flatten)
            .Single(node => node.FullTopic == "a/b");

        row.ObservedTopicPaths.Should().Equal("a//b", "a/b");
    }

    private static IEnumerable<TopicNodeSnapshot> Flatten(TopicNodeSnapshot node) =>
        node.Children.SelectMany(Flatten).Prepend(node);

    private static IEnumerable<string?> Payloads(TopicTreeStore store, SelectedTopicState state) =>
        store.GetSelectedMessages(state.Token, 100)!.Messages.Select(message => message.Payload);

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
