using Microsoft.Extensions.Logging;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Mqtt;

namespace MqttProbe.Core.Tests.Services.Mqtt;

[TestFixture]
public sealed class TopicTreeStoreMetricSelectionTests
{
    private const string GroupPath = "spBv1.0/plant1";
    private const string NodePath = "spBv1.0/plant1/NDATA/node1";

    [Test]
    public void NodeOwner_MatchesExactMetricAndCaseOnTheNodeEndpointOnly()
    {
        var store = CreateStore();
        Add(store, NodePath, "node-A", 1, "A");
        Add(store, NodePath, "node-A/B", 2, "A/B");
        Add(store, NodePath, "node-lower", 3, "a");
        Add(store, "spBv1.0/plant1/DDATA/node1/dev1", "device-A", 4, "A");
        Add(store, "spBv1.0/plant1/NDATA/node2", "other-node-A", 5, "A");
        Add(store, "spBv1.0/plant2/NDATA/node1", "other-group-A", 6, "A");

        SelectMetric(store, NodeScope("node1"), "A");
        var state = store.GetSelectedTopicState();
        state.Mode.Should().Be(TopicSelectionMode.SparkplugMetric);
        state.Metric.Should().Be(new MqttSparkplugMetricTarget(NodeScope("node1"), "A"));
        state.MessageCount.Should().Be(1);
        state.Paths.Should().BeEmpty();
        state.Branches.Should().BeEmpty();
        Payloads(store, state).Should().Equal("node-A");

        SelectMetric(store, NodeScope("node1"), "A/B");
        Payloads(store, store.GetSelectedTopicState()).Should().Equal("node-A/B");

        SelectMetric(store, NodeScope("node1"), "a");
        Payloads(store, store.GetSelectedTopicState()).Should().Equal("node-lower");
    }

    [Test]
    public void NodeAndDeviceOwners_AreIsolated()
    {
        var store = CreateStore();
        Add(store, NodePath, "node-A", 1, "A");
        Add(store, "spBv1.0/plant1/DDATA/node1/dev1", "dev1-A", 2, "A");
        Add(store, "spBv1.0/plant1/DDATA/node1/dev2", "dev2-A", 3, "A");

        SelectMetric(store, NodeScope("node1"), "A");
        store.GetSelectedTopicState().MessageCount.Should().Be(1);
        Payloads(store, store.GetSelectedTopicState()).Should().Equal("node-A");

        SelectMetric(store, DeviceScope("node1", "dev1"), "A");
        store.GetSelectedTopicState().MessageCount.Should().Be(1);
        Payloads(store, store.GetSelectedTopicState()).Should().Equal("dev1-A");
    }

    [Test]
    public void DynamicTarget_CountsOnlyMatchingArrivalsAndPersistsWhileEmpty()
    {
        var store = CreateStore();
        SelectMetric(store, NodeScope("node1"), "A");

        var empty = store.GetSelectedTopicState();
        empty.MessageCount.Should().Be(0);
        empty.Key.Should().NotBeNull();

        Add(store, "spBv1.0/plant1/NDATA/node2", "other-node-A", 1, "A");
        Add(store, NodePath, "wrong-metric", 2, "B");

        var afterUnrelated = store.GetSelectedTopicState();
        afterUnrelated.MessageCount.Should().Be(0);
        afterUnrelated.Token.Generation.Should().Be(empty.Token.Generation);
        afterUnrelated.Token.ContentVersion.Should().Be(empty.Token.ContentVersion);

        Add(store, NodePath, "matching-1", 3, "A");
        var afterMatch = store.GetSelectedTopicState();
        afterMatch.MessageCount.Should().Be(1);
        afterMatch.Token.ContentVersion.Should().BeGreaterThan(empty.Token.ContentVersion);

        Add(store, NodePath, "matching-2", 4, "A");
        store.GetSelectedTopicState().MessageCount.Should().Be(2);
    }

    [Test]
    public void GetSelectedMessages_ReturnsNewestFilteredMessagesAndReportsTheFullCount()
    {
        var store = CreateStore();
        Add(store, NodePath, "old", 1, "A");
        Add(store, NodePath, "mid", 2, "A");
        Add(store, NodePath, "new", 3, "A");
        Add(store, "sensors/temp", "unrelated", 4, "A");

        SelectMetric(store, NodeScope("node1"), "A");
        var state = store.GetSelectedTopicState();
        state.MessageCount.Should().Be(3);

        var limited = store.GetSelectedMessages(state.Token, 2)!;
        limited.Messages.Select(message => message.Payload).Should().Equal("new", "mid");
        limited.State.MessageCount.Should().Be(3);

        var zero = store.GetSelectedMessages(state.Token, 0)!;
        zero.Messages.Should().BeEmpty();
        zero.State.MessageCount.Should().Be(3);
    }

    [Test]
    public void Retention_AdvancesForMatchingEvictionsButNotUnrelatedOnes()
    {
        var store = CreateStore(maxMessages: 2);
        Add(store, NodePath, "matching", 1, "A");
        Add(store, "sensors/temp", "unrelated-1", 2, "A");
        SelectMetric(store, NodeScope("node1"), "A");
        var selected = store.GetSelectedTopicState();

        Add(store, "sensors/temp", "unrelated-2", 3, "A");
        var afterMatchingEviction = store.GetSelectedTopicState();
        afterMatchingEviction.MessageCount.Should().Be(0);
        afterMatchingEviction.Token.ContentVersion.Should().BeGreaterThan(selected.Token.ContentVersion);

        Add(store, "sensors/temp", "unrelated-3", 4, "A");
        store.GetSelectedTopicState().Token.ContentVersion
            .Should().Be(afterMatchingEviction.Token.ContentVersion,
                "evicting a message that does not match the metric does not change the selection");

        Add(store, NodePath, "matching-next", 5, "A");
        var afterMatch = store.GetSelectedTopicState();
        afterMatch.MessageCount.Should().Be(1);
        afterMatch.Token.ContentVersion.Should().BeGreaterThan(afterMatchingEviction.Token.ContentVersion);
    }

    [Test]
    public void PurgeAndOwnerClearKeepTheMetricSelectionWhileClearAllInvalidatesIt()
    {
        var store = CreateStore();
        Add(store, NodePath, "matching", 1, "A");
        Add(store, "sensors/temp", "unrelated", 2, "A");
        SelectMetric(store, NodeScope("node1"), "A");
        var selected = store.GetSelectedTopicState();

        store.RemoveMatchingTopic("sensors/temp");
        var afterPurge = store.GetSelectedTopicState();
        afterPurge.Key.Should().Be(selected.Key);
        afterPurge.Token.Generation.Should().Be(selected.Token.Generation);
        afterPurge.Token.ContentVersion.Should().BeGreaterThan(selected.Token.ContentVersion);
        afterPurge.MessageCount.Should().Be(1);

        store.ClearMessagesForTopics([NodePath]).Should().Be(1);
        var afterOwnerClear = store.GetSelectedTopicState();
        afterOwnerClear.Key.Should().Be(selected.Key);
        afterOwnerClear.Token.Generation.Should().Be(selected.Token.Generation);
        afterOwnerClear.MessageCount.Should().Be(0);

        store.Clear();
        var afterClearAll = store.GetSelectedTopicState();
        afterClearAll.Key.Should().BeNull();
        afterClearAll.Mode.Should().Be(TopicSelectionMode.SingleTopic);
        afterClearAll.Metric.Should().BeNull();
        store.GetSelectedMessages(selected.Token, 10).Should().BeNull();
    }

    [Test]
    public void SelectionTransitions_ClearTheMetricTarget()
    {
        var store = CreateStore();
        Add(store, NodePath, "matching", 1, "A");
        Add(store, "sensors/temp", "topic", 2, "A");
        Add(store, "spBv1.0/plant1/DDATA/node1/dev1", "device", 3, "A");

        SelectMetric(store, NodeScope("node1"), "A");
        store.SelectTopic("sensors/temp");
        var topic = store.GetSelectedTopicState();
        topic.Mode.Should().Be(TopicSelectionMode.SingleTopic);
        topic.Metric.Should().BeNull();

        SelectMetric(store, NodeScope("node1"), "A");
        store.SelectTopics(TopicSelection.ForBranches(
            [new TopicBranchTarget(TopicBranchKind.Raw, "sensors")]));
        var branch = store.GetSelectedTopicState();
        branch.Mode.Should().Be(TopicSelectionMode.Branches);
        branch.Metric.Should().BeNull();
        branch.Branches.Should().ContainSingle();

        SelectMetric(store, NodeScope("node1"), "A");
        store.SelectTopics(TopicSelection.ForExactTopics([NodePath, "sensors/temp"]));
        var exact = store.GetSelectedTopicState();
        exact.Mode.Should().Be(TopicSelectionMode.MultipleTopics);
        exact.Metric.Should().BeNull();
        exact.Paths.Should().HaveCount(2);
    }

    [Test]
    public void SwitchingMetricTargets_AdvancesGenerationAndContent()
    {
        var store = CreateStore();
        Add(store, NodePath, "matching", 1, "A");
        SelectMetric(store, NodeScope("node1"), "A");
        var first = store.GetSelectedTopicState();

        SelectMetric(store, DeviceScope("node1", "dev1"), "A");
        var second = store.GetSelectedTopicState();

        second.Token.Generation.Should().BeGreaterThan(first.Token.Generation);
        second.Token.ContentVersion.Should().BeGreaterThan(first.Token.ContentVersion);
        second.Metric.Should().Be(new MqttSparkplugMetricTarget(DeviceScope("node1", "dev1"), "A"));
        second.MessageCount.Should().Be(0);
    }

    [Test]
    public void MetricCountCache_ReusesUntilSelectionContentChanges()
    {
        var store = CreateStore();
        var first = Add(store, NodePath, "first", 1, "A");
        var second = Add(store, NodePath, "second", 2, "A");
        Add(store, NodePath, "third", 3, "A");
        SelectMetric(store, NodeScope("node1"), "A");
        var selected = store.GetSelectedTopicState();
        selected.MessageCount.Should().Be(3);

        first.Topic = "spBv1.0/plant1/NDATA/node9";
        second.Topic = "spBv1.0/plant1/NDATA/node9";

        store.GetSelectedTopicState().MessageCount.Should().Be(3,
            "an unchanged selection reuses the cached count even when a scan would now differ");
        Add(store, NodePath, "other-metric", 4, "B");
        var afterUnrelated = store.GetSelectedTopicState();
        afterUnrelated.MessageCount.Should().Be(3, "unrelated traffic does not invalidate the count");
        afterUnrelated.Token.ContentVersion.Should().Be(selected.Token.ContentVersion);

        Add(store, NodePath, "fourth", 5, "A");
        var afterMatch = store.GetSelectedTopicState();
        afterMatch.Token.ContentVersion.Should().BeGreaterThan(selected.Token.ContentVersion);
        afterMatch.MessageCount.Should().Be(2, "a matching arrival invalidates and recomputes the count");
    }

    [Test]
    public void MetricCountCache_InvalidatesOnMatchingEviction()
    {
        var store = CreateStore(maxMessages: 2);
        Add(store, NodePath, "matching", 1, "A");
        Add(store, NodePath, "survivor", 2, "A");
        SelectMetric(store, NodeScope("node1"), "A");
        var selected = store.GetSelectedTopicState();
        selected.MessageCount.Should().Be(2);

        Add(store, "sensors/temp", "unrelated", 3, "B");

        var after = store.GetSelectedTopicState();
        after.Token.ContentVersion.Should().BeGreaterThan(selected.Token.ContentVersion);
        after.MessageCount.Should().Be(1, "evicting a matching message invalidates and recomputes the count");
    }

    [Test]
    public void MetricCountCache_InvalidatesOnPurgeOwnerClearAndSelectionTransition()
    {
        var store = CreateStore();
        Add(store, NodePath, "first", 1, "A");
        Add(store, NodePath, "second", 2, "A");
        SelectMetric(store, NodeScope("node1"), "A");
        store.GetSelectedTopicState().MessageCount.Should().Be(2);

        store.RemoveMatchingTopic(NodePath);
        store.GetSelectedTopicState().MessageCount.Should().Be(0, "a purge invalidates the cached count");

        Add(store, NodePath, "first-again", 3, "A");
        Add(store, NodePath, "second-again", 4, "A");
        store.GetSelectedTopicState().MessageCount.Should().Be(2);

        store.ClearMessagesForTopics([NodePath]).Should().Be(2);
        store.GetSelectedTopicState().MessageCount.Should().Be(0, "an owner clear invalidates the cached count");

        Add(store, "spBv1.0/plant1/DDATA/node1/dev1", "device", 5, "A");
        store.GetSelectedTopicState().MessageCount.Should().Be(0);

        SelectMetric(store, DeviceScope("node1", "dev1"), "A");
        store.GetSelectedTopicState().MessageCount.Should().Be(1,
            "a selection transition invalidates the cached count");
    }

    private static void SelectMetric(TopicTreeStore store, SparkplugSelectionScope scope, string metricName) =>
        store.SelectTopics(TopicSelection.ForSparkplugMetric(scope, metricName));

    private static SparkplugSelectionScope NodeScope(string nodeId) =>
        new(SparkplugSelectionKind.Node, GroupPath, nodeId);

    private static SparkplugSelectionScope DeviceScope(string nodeId, string deviceId) =>
        new(SparkplugSelectionKind.Device, GroupPath, nodeId, deviceId);

    private static MqttMessage Add(TopicTreeStore store, string topic, string payload, int second,
        params string[] metricNames)
    {
        var message = new MqttMessage
        {
            Payload = payload,
            Topic = topic,
            DateTimeReceived = DateTime.UnixEpoch.AddSeconds(second),
            QualityOfServiceLevel = MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce,
            SparkplugMetricNames = [.. metricNames]
        };
        store.Add(topic, message);
        return message;
    }

    private static List<string?> Payloads(TopicTreeStore store, SelectedTopicState state) =>
        store.GetSelectedMessages(state.Token, 100)!.Messages.Select(message => message.Payload).ToList();

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
