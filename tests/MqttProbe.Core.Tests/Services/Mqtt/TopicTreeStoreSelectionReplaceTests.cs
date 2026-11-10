using Microsoft.Extensions.Logging;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Mqtt;

namespace MqttProbe.Core.Tests.Services.Mqtt;

[TestFixture]
public sealed class TopicTreeStoreSelectionReplaceTests
{
    private const string GroupPath = "spBv1.0/plant1";
    private const string NodePath = "spBv1.0/plant1/NDATA/node1";

    [Test]
    public void MatchingTreeStoreGenerationAndKey_AppliesTheReplacement()
    {
        var store = CreateStore();
        Add(store, NodePath, "A", 1, "A");
        Add(store, NodePath, "B", 2, "B");
        store.SelectTopics(MetricSelection("A"));
        var expected = store.GetSelectedTopicState();
        var version = store.Version;

        store.TryReplaceSelection(version, expected, MetricSelection("B")).Should().BeTrue();

        var state = store.GetSelectedTopicState();
        state.Mode.Should().Be(TopicSelectionMode.SparkplugMetric);
        state.Metrics.Should().Equal(Target("B"));
        state.Token.Generation.Should().Be(expected.Token.Generation + 1);
        state.MessageCount.Should().Be(1);
        Payloads(store, state).Should().Equal("B");
    }

    [Test]
    public void StaleTreeVersion_RejectsWithoutMutation()
    {
        var store = CreateStore();
        Add(store, NodePath, "A", 1, "A");
        store.SelectTopics(MetricSelection("A"));
        var expected = store.GetSelectedTopicState();
        var staleVersion = store.Version;

        Add(store, "sensors/temp", "unrelated", 2, "A");
        var before = store.GetSelectedTopicState();

        store.TryReplaceSelection(staleVersion, expected, MetricSelection("B")).Should().BeFalse();
        store.GetSelectedTopicState().Should().Be(before);
    }

    [Test]
    public void ForeignStoreToken_RejectsWithoutMutation()
    {
        var store = CreateStore();
        Add(store, NodePath, "A", 1, "A");
        store.SelectTopics(MetricSelection("A"));
        var current = store.GetSelectedTopicState();

        var foreignStore = CreateStore();
        Add(foreignStore, NodePath, "A", 1, "A");
        foreignStore.SelectTopics(MetricSelection("A"));
        var foreign = foreignStore.GetSelectedTopicState();
        var version = store.Version;

        store.TryReplaceSelection(version, foreign, MetricSelection("B")).Should().BeFalse();
        store.GetSelectedTopicState().Should().Be(current);
    }

    [Test]
    public void StaleGeneration_RejectsEvenAfterTheSameSelectionReturns()
    {
        var store = CreateStore();
        Add(store, NodePath, "A", 1, "A");
        Add(store, NodePath, "B", 2, "B");
        store.SelectTopics(MetricSelection("A"));
        var expected = store.GetSelectedTopicState();

        store.SelectTopics(MetricSelection("B"));
        store.SelectTopics(MetricSelection("A"));
        var before = store.GetSelectedTopicState();

        store.TryReplaceSelection(store.Version, expected, MetricSelection("B")).Should().BeFalse();
        store.GetSelectedTopicState().Should().Be(before);
    }

    [Test]
    public void WrongKeyOrNoSelection_RejectsWithoutMutation()
    {
        var store = CreateStore();
        Add(store, NodePath, "A", 1, "A");
        store.SelectTopics(MetricSelection("A"));
        var current = store.GetSelectedTopicState();

        var wrongKey = current with { Key = "some-other-row" };
        store.TryReplaceSelection(store.Version, wrongKey, MetricSelection("B")).Should().BeFalse();
        store.GetSelectedTopicState().Should().Be(current);

        var emptyStore = CreateStore();
        emptyStore.Add(NodePath, Message("A", 1, NodePath, "A"));
        var noSelection = emptyStore.GetSelectedTopicState();

        emptyStore.TryReplaceSelection(emptyStore.Version, noSelection, MetricSelection("A")).Should().BeFalse();
        emptyStore.GetSelectedTopicState().Should().Be(noSelection);
    }

    [Test]
    public void ContentVersionAdvanceAlone_DoesNotReject()
    {
        var store = CreateStore();
        Add(store, NodePath, "A", 1, "A");
        store.SelectTopics(MetricSelection("A"));
        var expected = store.GetSelectedTopicState();
        var advanced = expected with
        {
            Token = expected.Token with { ContentVersion = expected.Token.ContentVersion + 3 }
        };

        store.TryReplaceSelection(store.Version, advanced, MetricSelection("B")).Should().BeTrue();
        store.GetSelectedTopicState().Token.Generation.Should().Be(expected.Token.Generation + 1);
    }

    [Test]
    public void NullReplacement_ClearsWithTryClearSemantics()
    {
        var store = CreateStore();
        Add(store, NodePath, "A", 1, "A");
        store.SelectTopics(MetricSelection("A"));
        var expected = store.GetSelectedTopicState();
        var version = store.Version;

        store.TryReplaceSelection(version, expected, null).Should().BeTrue();

        var cleared = store.GetSelectedTopicState();
        cleared.Key.Should().BeNull();
        cleared.Metrics.Should().BeEmpty();
        cleared.Token.Generation.Should().Be(expected.Token.Generation + 1);

        store.TryReplaceSelection(version, expected, null).Should().BeFalse(
            "the same captured selection cannot be cleared twice");
        store.GetSelectedTopicState().Should().Be(cleared);
    }

    private static TopicSelection MetricSelection(string metricName) =>
        TopicSelection.ForSparkplugMetrics([Target(metricName)]);

    private static MqttSparkplugMetricTarget Target(string metricName) =>
        new(new SparkplugSelectionScope(SparkplugSelectionKind.Node, GroupPath, "node1"), metricName);

    private static MqttMessage Add(TopicTreeStore store, string topic, string payload, int second,
        params string[] metricNames)
    {
        var message = Message(payload, second, topic, metricNames);
        store.Add(topic, message);
        return message;
    }

    private static MqttMessage Message(string payload, int second, string topic, params string[] metricNames) => new()
    {
        Payload = payload,
        Topic = topic,
        DateTimeReceived = DateTime.UnixEpoch.AddSeconds(second),
        QualityOfServiceLevel = MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce,
        SparkplugMetricNames = [.. metricNames]
    };

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
