using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Mqtt;

namespace MqttProbe.Core.Tests.Services.Mqtt;

[TestFixture]
public sealed class TopicTreeStoreMetricUnionSelectionTests
{
    private const string GroupPath = "spBv1.0/plant1";
    private const string NodePath = "spBv1.0/plant1/NDATA/node1";
    private const string DevicePath = "spBv1.0/plant1/DDATA/node1/dev1";

    [Test]
    public void MultiSelection_UnionsNodeAndDeviceOwnersWithoutDuplicatingMessages()
    {
        var store = CreateStore();
        Add(store, NodePath, "node-A", 1, "A");
        Add(store, DevicePath, "dev1-A", 2, "A");
        Add(store, "spBv1.0/plant1/DDATA/node1/dev2", "dev2-A", 3, "A");
        Add(store, "spBv1.0/plant1/NDATA/node2", "node2-A", 4, "A");

        var selection = TopicSelection.ForSparkplugMetrics(
        [
            Target(NodeScope("node1"), "A"),
            Target(DeviceScope("node1", "dev1"), "A")
        ]);
        store.SelectTopics(selection);

        var state = store.GetSelectedTopicState();
        state.Mode.Should().Be(TopicSelectionMode.SparkplugMetric);
        state.Metrics.Should().Equal(selection.Metrics);
        state.MessageCount.Should().Be(2);
        Payloads(store, state).Should().BeEquivalentTo(["node-A", "dev1-A"]);
    }

    [Test]
    public void MultiSelection_CountsAPayloadOnceWhenItCarriesSeveralSelectedMetrics()
    {
        var store = CreateStore();
        Add(store, NodePath, "both", 1, "A", "B");
        Add(store, NodePath, "only-A", 2, "A");
        Add(store, NodePath, "only-B", 3, "B");

        store.SelectTopics(TopicSelection.ForSparkplugMetrics(
        [
            Target(NodeScope("node1"), "A"),
            Target(NodeScope("node1"), "B")
        ]));

        var state = store.GetSelectedTopicState();
        state.MessageCount.Should().Be(3);
        Payloads(store, state).Should().BeEquivalentTo(["both", "only-A", "only-B"]);
    }

    [Test]
    public void MultiSelection_KeepsOwnerKindCaseAndSlashesExact()
    {
        var store = CreateStore();
        Add(store, NodePath, "upper-A", 1, "A");
        Add(store, NodePath, "lower-a", 2, "a");
        Add(store, NodePath, "slashed", 3, "A/B");
        Add(store, DevicePath, "device-A", 4, "A");

        store.SelectTopics(TopicSelection.ForSparkplugMetrics(
        [
            Target(NodeScope("node1"), "A"),
            Target(DeviceScope("node1", "dev1"), "A")
        ]));
        Payloads(store, store.GetSelectedTopicState()).Should().BeEquivalentTo(["upper-A", "device-A"]);

        store.SelectTopics(TopicSelection.ForSparkplugMetrics([Target(NodeScope("node1"), "a")]));
        Payloads(store, store.GetSelectedTopicState()).Should().Equal("lower-a");

        store.SelectTopics(TopicSelection.ForSparkplugMetrics([Target(NodeScope("node1"), "A/B")]));
        Payloads(store, store.GetSelectedTopicState()).Should().Equal("slashed");
    }

    [Test]
    public void EquivalentMetricSets_DoNotAdvanceGenerationOnReorderingOrDuplicates()
    {
        var store = CreateStore();
        Add(store, NodePath, "A", 1, "A");
        Add(store, NodePath, "B", 2, "B");

        var first = TopicSelection.ForSparkplugMetrics(
        [
            Target(NodeScope("node1"), "A"),
            Target(NodeScope("node1"), "B")
        ]);
        store.SelectTopics(first);
        var selected = store.GetSelectedTopicState();

        store.SelectTopics(TopicSelection.ForSparkplugMetrics(
        [
            Target(NodeScope("node1"), "B"),
            Target(NodeScope("node1"), "A"),
            Target(NodeScope("node1"), "B")
        ]));

        var after = store.GetSelectedTopicState();
        after.Token.Generation.Should().Be(selected.Token.Generation);
        after.Token.ContentVersion.Should().Be(selected.Token.ContentVersion);
        after.Key.Should().Be(first.Key);
        after.Metrics.Should().Equal(selected.Metrics);

        store.SelectTopics(TopicSelection.ForSparkplugMetrics([Target(NodeScope("node1"), "A")]));
        store.GetSelectedTopicState().Token.Generation.Should().BeGreaterThan(selected.Token.Generation,
            "a membership change advances the generation");
    }

    [Test]
    public void MetricCountCache_IsReusedForAnEquivalentSetAndInvalidatedOnMembershipChange()
    {
        var store = CreateStore();
        var first = Add(store, NodePath, "A", 1, "A");
        Add(store, NodePath, "B", 2, "B");

        store.SelectTopics(TopicSelection.ForSparkplugMetrics([Target(NodeScope("node1"), "A")]));
        var selected = store.GetSelectedTopicState();
        selected.MessageCount.Should().Be(1);

        first.Topic = "spBv1.0/plant1/NDATA/node9";
        store.SelectTopics(TopicSelection.ForSparkplugMetrics(
        [
            Target(NodeScope("node1"), "A"),
            Target(NodeScope("node1"), "A")
        ]));
        store.GetSelectedTopicState().MessageCount.Should().Be(1,
            "the equivalent set reuses the cached count even though a rescan would now differ");

        store.SelectTopics(TopicSelection.ForSparkplugMetrics(
        [
            Target(NodeScope("node1"), "A"),
            Target(NodeScope("node1"), "B")
        ]));
        var after = store.GetSelectedTopicState();
        after.Token.Generation.Should().BeGreaterThan(selected.Token.Generation);
        after.MessageCount.Should().Be(1, "the membership change invalidates the cached count");
    }

    [Test]
    public void BranchAndMetricModes_ReplaceEachOtherWithoutLeakingMembership()
    {
        var store = CreateStore();
        Add(store, NodePath, "A", 1, "A");
        Add(store, "sensors/temp", "sensor", 2, "A");

        store.SelectTopics(TopicSelection.ForBranches([new TopicBranchTarget(TopicBranchKind.Raw, "spBv1.0")]));
        var branch = store.GetSelectedTopicState();
        branch.Mode.Should().Be(TopicSelectionMode.Branches);
        branch.Branches.Should().ContainSingle();
        branch.Metrics.Should().BeEmpty();

        store.SelectTopics(TopicSelection.ForSparkplugMetrics([Target(NodeScope("node1"), "A")]));
        var metric = store.GetSelectedTopicState();
        metric.Mode.Should().Be(TopicSelectionMode.SparkplugMetric);
        metric.Branches.Should().BeEmpty();
        metric.Metrics.Should().ContainSingle();
        metric.Metric.Should().Be(Target(NodeScope("node1"), "A"));

        store.SelectTopics(TopicSelection.ForBranches([new TopicBranchTarget(TopicBranchKind.Raw, "sensors")]));
        var backToBranch = store.GetSelectedTopicState();
        backToBranch.Mode.Should().Be(TopicSelectionMode.Branches);
        backToBranch.Metrics.Should().BeEmpty();
        backToBranch.Metric.Should().BeNull();
        Payloads(store, backToBranch).Should().Equal("sensor");
    }

    [Test]
    public void EmptyMetricSet_ClearsTheSelectionAndLeavesNoEmptyMetricMode()
    {
        var store = CreateStore();
        Add(store, NodePath, "A", 1, "A");
        store.SelectTopics(TopicSelection.ForSparkplugMetrics([Target(NodeScope("node1"), "A")]));
        var selected = store.GetSelectedTopicState();
        selected.MessageCount.Should().Be(1);

        store.SelectTopics(TopicSelection.ForSparkplugMetrics([]));

        var cleared = store.GetSelectedTopicState();
        cleared.Key.Should().BeNull();
        cleared.Mode.Should().Be(TopicSelectionMode.SingleTopic);
        cleared.Metrics.Should().BeEmpty();
        cleared.Metric.Should().BeNull();
        cleared.MessageCount.Should().Be(0);
        store.GetSelectedMessages(selected.Token, 10).Should().BeNull();
    }

    [Test]
    public void InvalidMetricTarget_DoesNotMutateTheCurrentSelection()
    {
        var store = CreateStore();
        Add(store, NodePath, "A", 1, "A");
        store.SelectTopics(TopicSelection.ForSparkplugMetrics([Target(NodeScope("node1"), "A")]));
        var before = store.GetSelectedTopicState();

        var invalid = new TopicSelection("invalid", "invalid", ImmutableArray<string>.Empty)
        {
            Mode = TopicSelectionMode.SparkplugMetric,
            Metrics =
            [
                Target(NodeScope("node1"), "A"),
                new MqttSparkplugMetricTarget(
                    new SparkplugSelectionScope(SparkplugSelectionKind.Devices, GroupPath, "node1"), "B")
            ]
        };

        var act = () => store.SelectTopics(invalid);
        act.Should().Throw<ArgumentException>();
        store.GetSelectedTopicState().Should().Be(before);
    }

    private static MqttSparkplugMetricTarget Target(SparkplugSelectionScope scope, string name) => new(scope, name);

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
