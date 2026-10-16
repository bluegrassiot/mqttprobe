using Microsoft.Extensions.Logging;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Mqtt;

namespace MqttProbe.Core.Tests.Services.Mqtt;

[TestFixture]
public sealed class TopicTreeStoreSelectionIndexTests
{
    private const string GroupPath = "spBv1.0/plant1";

    [Test]
    public void ContentVersion_AdvancesForRootDescendantAndAncestorTraffic()
    {
        var store = CreateStore();
        AddCanonical(store, "spBv1.0/plant1/NDATA/node1", "node", 1);
        store.SelectTopics(NodeScope("node1"));
        var selected = store.GetSelectedTopicState();

        AddCanonical(store, "spBv1.0/plant1/NDATA/node1", "root-traffic", 2);
        var afterRoot = store.GetSelectedTopicState();
        afterRoot.Token.ContentVersion.Should().BeGreaterThan(selected.Token.ContentVersion);
        afterRoot.Token.Generation.Should().Be(selected.Token.Generation);

        AddCanonical(store, "spBv1.0/plant1/NDATA/node1/deeper", "descendant", 3);
        var afterDescendant = store.GetSelectedTopicState();
        afterDescendant.Token.ContentVersion.Should().BeGreaterThan(afterRoot.Token.ContentVersion);

        AddCanonical(store, "spBv1.0/plant1/NDATA", "ancestor", 4);
        var afterAncestor = store.GetSelectedTopicState();
        afterAncestor.Token.ContentVersion.Should().BeGreaterThan(afterDescendant.Token.ContentVersion);
        afterAncestor.Token.Generation.Should().Be(selected.Token.Generation);
    }

    [Test]
    public void SiblingTrafficUnderASharedAncestor_LeavesContentVersionUnchanged()
    {
        var store = CreateStore();
        AddCanonical(store, "spBv1.0/plant1/NDATA/node1", "node", 1);
        store.SelectTopics(NodeScope("node1"));
        var selected = store.GetSelectedTopicState();

        AddCanonical(store, "spBv1.0/plant1/NDATA/node2", "sibling", 2);
        AddCanonical(store, "spBv1.0/plant1/NBIRTH/node2", "other-verb-sibling", 3);

        var after = store.GetSelectedTopicState();
        after.Token.ContentVersion.Should().Be(selected.Token.ContentVersion,
            "a sibling under a shared ancestor is not part of the selection");
        after.Token.Generation.Should().Be(selected.Token.Generation);
        after.MessageCount.Should().Be(1);
    }

    [Test]
    public void SwitchAndClear_DropTheOldAncestorIndex()
    {
        var store = CreateStore();
        AddCanonical(store, "spBv1.0/plant1/NDATA/node1", "node", 1);
        AddCanonical(store, "sensors/temp", "sensors", 2);
        store.SelectTopics(NodeScope("node1"));

        store.SelectTopic("sensors/temp");
        var afterSwitch = store.GetSelectedTopicState();
        afterSwitch.Key.Should().Be("sensors/temp");

        AddCanonical(store, "spBv1.0/plant1/NDATA", "ancestor", 3);
        store.GetSelectedTopicState().Token.ContentVersion.Should().Be(afterSwitch.Token.ContentVersion,
            "the ancestors of the selection that was switched away must not match");

        store.SelectTopics(NodeScope("node1"));
        store.SelectTopics(null);
        var afterClear = store.GetSelectedTopicState();

        AddCanonical(store, "spBv1.0/plant1/NDATA", "ancestor-again", 4);
        store.GetSelectedTopicState().Token.ContentVersion.Should().Be(afterClear.Token.ContentVersion,
            "the ancestors of a cleared selection must not match");
    }

    [Test]
    public void ANewScopedEndpoint_UpdatesBothIndexesWithoutMovingTheGeneration()
    {
        var store = CreateStore();
        AddCanonical(store, "spBv1.0/plant1/NDATA/node1", "node", 1);
        store.SelectTopics(NodeScope("node1"));
        var selected = store.GetSelectedTopicState();

        AddCanonical(store, "spBv1.0/plant1/DDATA/node1/dev1", "dev1", 2);

        var grown = store.GetSelectedTopicState();
        grown.Token.Generation.Should().Be(selected.Token.Generation);
        grown.Token.ContentVersion.Should().BeGreaterThan(selected.Token.ContentVersion);
        grown.MessageCount.Should().Be(2, "the root set gained the new endpoint");

        AddCanonical(store, "spBv1.0/plant1/DDATA", "ancestor", 3);
        store.GetSelectedTopicState().Token.ContentVersion.Should().BeGreaterThan(grown.Token.ContentVersion,
            "the ancestor set gained the new endpoint's chain");
    }

    [Test]
    public void Retention_EvictingSelectedContentAdvancesButUnrelatedEvictionDoesNot()
    {
        var store = CreateStore(maxMessages: 2);
        AddCanonical(store, "sensors/temp", "unrelated-oldest", 1);
        AddCanonical(store, "spBv1.0/plant1/NDATA/node1", "selected", 2);
        store.SelectTopics(NodeScope("node1"));
        var selected = store.GetSelectedTopicState();

        AddCanonical(store, "sensors/temp", "unrelated-next", 3);
        var afterUnrelated = store.GetSelectedTopicState();
        afterUnrelated.Token.ContentVersion.Should().Be(selected.Token.ContentVersion);
        afterUnrelated.MessageCount.Should().Be(1);

        AddCanonical(store, "sensors/temp", "unrelated-third", 4);
        var afterSelected = store.GetSelectedTopicState();
        afterSelected.Token.ContentVersion.Should().BeGreaterThan(selected.Token.ContentVersion,
            "evicting a selected path's message changes the selection");
        afterSelected.Token.Generation.Should().Be(selected.Token.Generation);
    }

    [Test]
    public void AnUnrelatedPurge_KeepsTheSelection()
    {
        var store = CreateStore();
        AddCanonical(store, "spBv1.0/plant1/NDATA/node1", "node", 1);
        AddCanonical(store, "sensors/temp", "sensors", 2);
        store.SelectTopics(NodeScope("node1"));
        var selected = store.GetSelectedTopicState();

        store.RemoveMatchingTopic("sensors/temp");

        var after = store.GetSelectedTopicState();
        after.Key.Should().Be(selected.Key);
        after.Token.Generation.Should().Be(selected.Token.Generation);
        after.Token.ContentVersion.Should().BeGreaterThan(selected.Token.ContentVersion);
        after.MessageCount.Should().Be(1);
        store.GetSelectedMessages(selected.Token, 10)!.Messages.Should().ContainSingle();
    }

    [Test]
    public void PurgeThatDropsASelectedRootOrItsCanonicalMarker_ClearsTheSelection()
    {
        var removedRoot = CreateStore();
        AddCanonical(removedRoot, "spBv1.0/plant1/NDATA/node1", "node", 1);
        AddCanonical(removedRoot, "spBv1.0/plant1/DDATA/node1/dev1", "dev1", 2);
        removedRoot.SelectTopics(NodeScope("node1"));
        removedRoot.RemoveMatchingTopic("spBv1.0/plant1/NDATA/node1");
        removedRoot.GetSelectedTopicState().Key.Should().BeNull();

        var retiredMarker = CreateStore();
        AddCanonical(retiredMarker, "spBv1.0/plant1/NDATA/node1", "node", 1);
        AddCanonical(retiredMarker, "spBv1.0/plant1/NDATA/node1/extra", "extra", 2);
        retiredMarker.SelectTopics(NodeScope("node1"));
        retiredMarker.RemoveMatchingTopic("spBv1.0/plant1/NDATA/node1");
        retiredMarker.GetSelectedTopicState().Key.Should().BeNull();
    }

    [Test]
    public void ALargeScopedSelection_ReadsEachEndpointOnce()
    {
        const int devices = 30;
        var store = CreateStore(maxMessages: 100);
        AddCanonical(store, "spBv1.0/plant1/NDATA/node1", "node", 1);
        for (var i = 0; i < devices; i++)
            AddCanonical(store, $"spBv1.0/plant1/DDATA/node1/dev{i}", $"device-{i}", i + 2);

        store.SelectTopics(NodeScope("node1"));

        var state = store.GetSelectedTopicState();
        state.MessageCount.Should().Be(devices + 1);
        var payloads = store.GetSelectedMessages(state.Token, 100)!.Messages
            .Select(message => message.Payload).ToList();
        payloads.Should().HaveCount(devices + 1, "every endpoint is read exactly once");
        payloads.Should().OnlyHaveUniqueItems();
    }

    private static TopicSelection NodeScope(string nodeId) =>
        new($"\0spb:node:{GroupPath}/{nodeId}", $"{GroupPath}/{nodeId}", [],
            new SparkplugSelectionScope(SparkplugSelectionKind.Node, GroupPath, nodeId));

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
