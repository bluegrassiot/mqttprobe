using Microsoft.Extensions.Logging;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Mqtt;

namespace MqttProbe.Core.Tests.Services.Mqtt;

[TestFixture]
public sealed class TopicTreeStoreBranchSelectionTests
{
    private const string GroupPath = "spBv1.0/plant1";

    [Test]
    public void RawBranch_CoversSelfAndDescendantsOnly()
    {
        var store = CreateStore();
        AddCanonical(store, "a/b", "self", 1);
        AddCanonical(store, "a/b/c", "child", 2);
        AddCanonical(store, "a/bc", "sibling", 3);

        SelectBranches(store, new TopicBranchTarget(TopicBranchKind.Raw, "a/b"));

        var state = store.GetSelectedTopicState();
        state.Mode.Should().Be(TopicSelectionMode.Branches);
        state.MessageCount.Should().Be(2);
        Payloads(store, state).Should().BeEquivalentTo(["self", "child"]);
    }

    [Test]
    public void RawBranch_KeepsEmptyLevelsDistinct()
    {
        var store = CreateStore();
        AddCanonical(store, "a/b", "plain", 1);
        AddCanonical(store, "a//b", "empty-level", 2);

        SelectBranches(store, new TopicBranchTarget(TopicBranchKind.Raw, "a//b"));

        Payloads(store, store.GetSelectedTopicState()).Should().Equal("empty-level");
    }

    [Test]
    public void RawBranch_KeepsLeadingAndTrailingSlashDistinct()
    {
        var store = CreateStore();
        AddCanonical(store, "a/b", "plain", 1);
        AddCanonical(store, "/a/b/", "slashed", 2);

        SelectBranches(store, new TopicBranchTarget(TopicBranchKind.Raw, "/a/b/"));

        Payloads(store, store.GetSelectedTopicState()).Should().Equal("slashed");
    }

    [Test]
    public void RawEmptyPath_CoversOnlySlashLeadingTopics()
    {
        var store = CreateStore();
        AddCanonical(store, "a", "bare", 1);
        AddCanonical(store, "/a", "leading", 2);
        AddCanonical(store, "/", "root-only", 3);

        SelectBranches(store, new TopicBranchTarget(TopicBranchKind.Raw, string.Empty));

        Payloads(store, store.GetSelectedTopicState()).Should().BeEquivalentTo(["leading", "root-only"]);
    }

    [Test]
    public void StructuralRoot_IsResolvableAndNullClearsSelection()
    {
        var store = CreateStore();
        AddCanonical(store, "/a", "leading", 1);

        store.SelectTopic(string.Empty);

        var selected = store.GetSelectedTopicState();
        selected.Key.Should().BeEmpty();
        Payloads(store, selected).Should().Equal("leading");

        store.SelectTopic(null);

        var cleared = store.GetSelectedTopicState();
        cleared.Key.Should().BeNull();
        cleared.Paths.Should().BeEmpty();
    }

    [Test]
    public void MixedScopeAndBranch_UnionsWithoutDuplicates()
    {
        var store = CreateStore();
        AddCanonical(store, $"{GroupPath}/NDATA/node1", "node-data", 1);
        AddCanonical(store, $"{GroupPath}/DBIRTH/node1/dev1", "device-birth", 2);
        AddCanonical(store, $"{GroupPath}/DDATA/node1/dev1", "device-data", 3);

        SelectBranches(store,
            new TopicBranchTarget(TopicBranchKind.Raw, $"{GroupPath}/DDATA/node1/dev1"),
            new TopicBranchTarget(TopicBranchKind.Raw, $"{GroupPath}/NDATA/node1"),
            new TopicBranchTarget(TopicBranchKind.Sparkplug,
                Scope: Sparkplug(SparkplugSelectionKind.Devices, "node1")));

        var state = store.GetSelectedTopicState();
        state.MessageCount.Should().Be(3, "device-data is covered by two targets but counted once");
        Payloads(store, state).Should().BeEquivalentTo(["node-data", "device-birth", "device-data"]);
    }

    [Test]
    public void BranchSelection_IncludesArrivalsAfterSelection()
    {
        var store = CreateStore();
        SelectBranches(store, new TopicBranchTarget(TopicBranchKind.Raw, "a/b"));

        store.GetSelectedTopicState().MessageCount.Should().Be(0);

        AddCanonical(store, "a/b/late", "late", 1);

        var state = store.GetSelectedTopicState();
        state.MessageCount.Should().Be(1);
        Payloads(store, state).Should().Equal("late");
    }

    [Test]
    public void SparkplugMessageType_RestrictsTheVerb()
    {
        var store = CreateStore();
        AddCanonical(store, $"{GroupPath}/NBIRTH/node1", "birth", 1);
        AddCanonical(store, $"{GroupPath}/NDATA/node1", "data", 2);

        SelectBranches(store,
            new TopicBranchTarget(TopicBranchKind.Sparkplug,
                Scope: Sparkplug(SparkplugSelectionKind.Node, "node1"),
                MessageType: "NDATA"));

        Payloads(store, store.GetSelectedTopicState()).Should().Equal("data");
    }

    [Test]
    public void BranchSelection_ReadsNewestFirst()
    {
        var store = CreateStore();
        AddCanonical(store, "a/b", "old", 1);
        AddCanonical(store, "a/b", "new", 2);

        SelectBranches(store, new TopicBranchTarget(TopicBranchKind.Raw, "a/b"));

        Payloads(store, store.GetSelectedTopicState()).Should().Equal("new", "old");
    }

    [Test]
    public void ClearBranch_RemovesCoveredMessagesAndPrunesEmptyNodes()
    {
        var store = CreateStore();
        AddCanonical(store, "a/b/c", "cleared", 1);
        AddCanonical(store, "a/b/d", "kept", 2);

        var removed = store.ClearMessagesForBranches([new TopicBranchTarget(TopicBranchKind.Raw, "a/b/c")]);

        removed.Should().Be(1);
        store.TotalStoredMessages.Should().Be(1);
        var paths = store.GetTopicTreeSnapshot().Roots.SelectMany(Flatten).Select(node => node.FullTopic).ToList();
        paths.Should().NotContain("a/b/c");
        paths.Should().Contain(["a", "a/b", "a/b/d"]);
    }

    [Test]
    public void ClearBranch_ZeroMessagesStillRetiresObservedHistory()
    {
        var store = CreateStore(maxMessages: 1);
        AddCanonical(store, "a/b", "evicted", 1);
        AddCanonical(store, "other", "newer", 2);

        store.TotalStoredMessages.Should().Be(1);

        var removed = store.ClearMessagesForBranches([new TopicBranchTarget(TopicBranchKind.Raw, "a/b")]);

        removed.Should().Be(0);
        store.GetTopicTreeSnapshot().Roots.SelectMany(Flatten)
            .Select(node => node.FullTopic)
            .Should().NotContain("a/b");
    }

    [Test]
    public void ClearBranch_KeepsSelectionAndRecreatesRows()
    {
        var store = CreateStore();
        AddCanonical(store, "a/b", "first", 1);
        SelectBranches(store, new TopicBranchTarget(TopicBranchKind.Raw, "a/b"));
        var selected = store.GetSelectedTopicState();

        store.ClearMessagesForBranches([new TopicBranchTarget(TopicBranchKind.Raw, "a/b")]);

        var cleared = store.GetSelectedTopicState();
        cleared.MessageCount.Should().Be(0);
        cleared.Key.Should().Be(selected.Key);
        Payloads(store, cleared).Should().BeEmpty();

        AddCanonical(store, "a/b", "second", 2);

        var recreated = store.GetSelectedTopicState();
        recreated.MessageCount.Should().Be(1);
        Payloads(store, recreated).Should().Equal("second");
    }

    [Test]
    public void ClearBranch_LeavesUnrelatedBranchesIntact()
    {
        var store = CreateStore();
        AddCanonical(store, "keep/a", "keep-a", 1);
        AddCanonical(store, "clear/b", "clear-b", 2);

        store.ClearMessagesForBranches([new TopicBranchTarget(TopicBranchKind.Raw, "clear/b")]);

        store.TotalStoredMessages.Should().Be(1);
        Payloads(store, "keep/a").Should().Equal("keep-a");
    }

    [Test]
    public void SparkplugNodeScope_IncludesDeviceEndpoints()
    {
        var store = CreateStore();
        AddCanonical(store, $"{GroupPath}/NBIRTH/node1", "birth", 1);
        AddCanonical(store, $"{GroupPath}/NDATA/node1", "node-data", 2);
        AddCanonical(store, $"{GroupPath}/DDATA/node1/dev1", "device-data", 3);
        AddCanonical(store, $"{GroupPath}/DBIRTH/node1/dev2", "device-birth", 4);

        SelectBranches(store,
            new TopicBranchTarget(TopicBranchKind.Sparkplug,
                Scope: Sparkplug(SparkplugSelectionKind.Node, "node1")));

        Payloads(store, store.GetSelectedTopicState())
            .Should().BeEquivalentTo(["birth", "node-data", "device-data", "device-birth"]);
    }

    [Test]
    public void SparkplugNodeScope_IncludesEndpointDescendants()
    {
        var store = CreateStore();
        AddCanonical(store, $"{GroupPath}/NDATA/node1", "node-data", 1);
        AddCanonical(store, $"{GroupPath}/DDATA/node1/dev1/child", "descendant", 2);
        AddCanonical(store, $"{GroupPath}/NDATA/node2", "other-node", 3);

        SelectBranches(store,
            new TopicBranchTarget(TopicBranchKind.Sparkplug,
                Scope: Sparkplug(SparkplugSelectionKind.Node, "node1")));

        Payloads(store, store.GetSelectedTopicState()).Should().BeEquivalentTo(["node-data", "descendant"]);
    }

    [Test]
    public void ClearBranch_SparkplugNodeScope_RemovesDeviceEndpoints()
    {
        var store = CreateStore();
        AddCanonical(store, $"{GroupPath}/DDATA/node1/dev1", "device", 1);
        AddCanonical(store, $"{GroupPath}/NDATA/node2", "other-node", 2);

        var removed = store.ClearMessagesForBranches([
            new TopicBranchTarget(TopicBranchKind.Sparkplug,
                Scope: Sparkplug(SparkplugSelectionKind.Node, "node1"))]);

        removed.Should().Be(1);
        store.TotalStoredMessages.Should().Be(1);
        store.GetTopicTreeSnapshot().Roots.SelectMany(Flatten).Select(node => node.FullTopic)
            .Should().Contain($"{GroupPath}/NDATA/node2");
    }

    [Test]
    public void BranchKey_DistinguishesDelimiterCollisions()
    {
        var first = TopicSelection.ForBranches([
            new TopicBranchTarget(TopicBranchKind.Sparkplug,
                Scope: new SparkplugSelectionScope(SparkplugSelectionKind.Node, "spBv1.0/g:x", "n"))
        ]);
        var second = TopicSelection.ForBranches([
            new TopicBranchTarget(TopicBranchKind.Sparkplug,
                Scope: new SparkplugSelectionScope(SparkplugSelectionKind.Node, "spBv1.0/g", "x:n"))
        ]);

        first.Key.Should().NotBe(second.Key);

        var store = CreateStore();
        store.SelectTopics(first);
        var firstKey = store.GetSelectedTopicState().Key;
        store.SelectTopics(second);

        var switched = store.GetSelectedTopicState().Key;
        switched.Should().Be(second.Key);
        switched.Should().NotBe(firstKey);
    }

    [Test]
    public void BranchSelection_EvictionOfNullTopicMessageBumpsContentVersion()
    {
        var store = CreateStore(maxMessages: 1);
        AddWithoutTopic(store, "a", "old", 1);
        SelectBranches(store, new TopicBranchTarget(TopicBranchKind.Raw, "a"));
        var before = store.GetSelectedTopicState();
        before.MessageCount.Should().Be(1);

        AddCanonical(store, "b", "new", 2);

        var after = store.GetSelectedTopicState();
        after.MessageCount.Should().Be(0);
        after.Token.ContentVersion.Should().BeGreaterThan(before.Token.ContentVersion);
    }

    [Test]
    public void ClearBranch_PrunesCoveredEmptyStructuralNodesFromPartialInsert()
    {
        var store = CreateStore(maxMessages: 100, maxTopicNodes: 2);
        AddCanonical(store, "a/b/c", "never-stored", 1);

        store.TotalStoredMessages.Should().Be(0);
        store.GetTopicTreeSnapshot().Roots.SelectMany(Flatten).Select(node => node.FullTopic)
            .Should().BeEquivalentTo(["a", "a/b"]);

        var removed = store.ClearMessagesForBranches([new TopicBranchTarget(TopicBranchKind.Raw, "a")]);

        removed.Should().Be(0);
        store.RootTopicCount.Should().Be(0);
        store.GetTopicTreeSnapshot().Roots.Should().BeEmpty();
    }

    [Test]
    public void BranchCount_TracksContentVersionAcrossAddEvictAndClear()
    {
        var store = CreateStore();
        AddCanonical(store, "a/b", "one", 1);
        SelectBranches(store, new TopicBranchTarget(TopicBranchKind.Raw, "a/b"));
        var first = store.GetSelectedTopicState();
        first.MessageCount.Should().Be(1);

        store.GetSelectedTopicState().MessageCount.Should().Be(1);

        AddCanonical(store, "a/b", "two", 2);
        var grown = store.GetSelectedTopicState();
        grown.MessageCount.Should().Be(2);
        grown.Token.ContentVersion.Should().BeGreaterThan(first.Token.ContentVersion);

        AddCanonical(store, "other", "unrelated", 3);
        store.GetSelectedTopicState().MessageCount.Should().Be(2);

        store.ClearMessagesForBranches([new TopicBranchTarget(TopicBranchKind.Raw, "a/b")]);
        store.GetSelectedTopicState().MessageCount.Should().Be(0);
    }

    [Test]
    public void DevicesScope_MatchesFutureDeviceVerbEndpointsAndDescendants()
    {
        var store = CreateStore();
        AddCanonical(store, $"{GroupPath}/DFUTURE/node1/dev1", "future", 1);
        AddCanonical(store, $"{GroupPath}/DFUTURE/node1/dev1/child", "future-descendant", 2);
        AddCanonical(store, $"{GroupPath}/DFUTURE/node1/dev2", "sibling-device", 3);

        SelectBranches(store,
            new TopicBranchTarget(TopicBranchKind.Sparkplug,
                Scope: Sparkplug(SparkplugSelectionKind.Devices, "node1")));

        Payloads(store, store.GetSelectedTopicState())
            .Should().BeEquivalentTo(["future", "future-descendant", "sibling-device"]);
    }

    [Test]
    public void DeviceScope_MatchesFutureDeviceVerbAndExcludesSiblingDevices()
    {
        var store = CreateStore();
        AddCanonical(store, $"{GroupPath}/DFUTURE/node1/dev1", "future", 1);
        AddCanonical(store, $"{GroupPath}/DFUTURE/node1/dev1/child", "future-descendant", 2);
        AddCanonical(store, $"{GroupPath}/DFUTURE/node1/dev2", "sibling", 3);

        SelectBranches(store,
            new TopicBranchTarget(TopicBranchKind.Sparkplug,
                Scope: Sparkplug(SparkplugSelectionKind.Device, "node1", "dev1")));

        Payloads(store, store.GetSelectedTopicState())
            .Should().BeEquivalentTo(["future", "future-descendant"]);
    }

    [Test]
    public void DevicesScope_DoesNotTreatNodeEndpointDescendantsAsDevices()
    {
        var store = CreateStore();
        AddCanonical(store, $"{GroupPath}/NDATA/node1/dev1", "node-endpoint-descendant", 1);
        AddCanonical(store, $"{GroupPath}/NDATA/node1/dev1/child", "deeper", 2);

        SelectBranches(store,
            new TopicBranchTarget(TopicBranchKind.Sparkplug,
                Scope: Sparkplug(SparkplugSelectionKind.Devices, "node1")));

        var state = store.GetSelectedTopicState();
        state.MessageCount.Should().Be(0);
        Payloads(store, state).Should().BeEmpty();
    }

    [Test]
    public void DeviceScope_ExplicitMessageTypeStaysStrict()
    {
        var store = CreateStore();
        AddCanonical(store, $"{GroupPath}/DFUTURE/node1/dev1", "future", 1);
        AddCanonical(store, $"{GroupPath}/DFUTURE/node1/dev2", "other-device", 2);
        AddCanonical(store, $"{GroupPath}/DDATA/node1/dev1", "known", 3);

        SelectBranches(store,
            new TopicBranchTarget(TopicBranchKind.Sparkplug,
                Scope: Sparkplug(SparkplugSelectionKind.Devices, "node1"),
                MessageType: "DFUTURE"));

        Payloads(store, store.GetSelectedTopicState()).Should().BeEquivalentTo(["future", "other-device"]);
    }

    private static SparkplugSelectionScope Sparkplug(
        SparkplugSelectionKind kind, string nodeId, string? deviceId = null) =>
        new(kind, GroupPath, nodeId, deviceId);

    private static void SelectBranches(TopicTreeStore store, params TopicBranchTarget[] targets) =>
        store.SelectTopics(TopicSelection.ForBranches(targets));

    private static IEnumerable<TopicNodeSnapshot> Flatten(TopicNodeSnapshot node) =>
        node.Children.SelectMany(Flatten).Prepend(node);

    private static IEnumerable<string?> Payloads(TopicTreeStore store, SelectedTopicState state) =>
        store.GetSelectedMessages(state.Token, 100)!.Messages.Select(message => message.Payload);

    private static IEnumerable<string?> Payloads(TopicTreeStore store, string topic)
    {
        store.SelectTopics(TopicSelection.ForExactTopics([topic]));
        var state = store.GetSelectedTopicState();
        return store.GetSelectedMessages(state.Token, 100)!.Messages.Select(message => message.Payload);
    }

    private static void AddCanonical(TopicTreeStore store, string topic, string payload, int second) =>
        store.Add(topic, new MqttMessage
        {
            Payload = payload,
            Topic = topic,
            DateTimeReceived = DateTime.UnixEpoch.AddSeconds(second),
            QualityOfServiceLevel = MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce
        });

    private static void AddWithoutTopic(TopicTreeStore store, string topic, string payload, int second) =>
        store.Add(topic, new MqttMessage
        {
            Payload = payload,
            DateTimeReceived = DateTime.UnixEpoch.AddSeconds(second),
            QualityOfServiceLevel = MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce
        });

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
}
