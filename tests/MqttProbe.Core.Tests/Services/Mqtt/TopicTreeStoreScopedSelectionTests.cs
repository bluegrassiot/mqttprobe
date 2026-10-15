using Microsoft.Extensions.Logging;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Mqtt;

namespace MqttProbe.Core.Tests.Services.Mqtt;

[TestFixture]
public sealed class TopicTreeStoreScopedSelectionTests
{
    private const string GroupPath = "spBv1.0/plant1";

    [Test]
    public void NodeScope_CoversNodeVerbsAndDeviceEndpoints()
    {
        var store = CreateStore();
        AddCanonical(store, "spBv1.0/plant1/NDATA/node1", "node-data", 1);
        AddCanonical(store, "spBv1.0/plant1/NBIRTH/node1", "birth", 2);
        AddCanonical(store, "spBv1.0/plant1/DDATA/node1/dev1", "dev1", 3);
        AddCanonical(store, "spBv1.0/plant1/DDATA/node1/dev2", "dev2", 4);
        AddCanonical(store, "sensors/temp", "unrelated", 5);

        store.SelectTopics(NodeScope("node1"));

        var state = store.GetSelectedTopicState();
        state.Key.Should().Be(NodeKey);
        state.Label.Should().Be($"{GroupPath}/node1");
        state.MessageCount.Should().Be(4, "the node row totals its own verbs and its devices");
        Payloads(store, state).Should().BeEquivalentTo(["node-data", "birth", "dev1", "dev2"]);
    }

    [Test]
    public void DevicesScope_CoversOnlyDeviceEndpoints()
    {
        var store = CreateStore();
        AddCanonical(store, "spBv1.0/plant1/NDATA/node1", "node-data", 1);
        AddCanonical(store, "spBv1.0/plant1/DDATA/node1/dev1", "dev1", 2);
        AddCanonical(store, "spBv1.0/plant1/DDATA/node1/dev2", "dev2", 3);

        store.SelectTopics(DevicesScope("node1"));

        var state = store.GetSelectedTopicState();
        state.MessageCount.Should().Be(2, "the node's own verbs are not device traffic");
        Payloads(store, state).Should().BeEquivalentTo(["dev1", "dev2"]);
    }

    [Test]
    public void DeviceScope_CoversEveryVerbOfThatDevice()
    {
        var store = CreateStore();
        AddCanonical(store, "spBv1.0/plant1/DBIRTH/node1/dev1", "dbirth", 1);
        AddCanonical(store, "spBv1.0/plant1/DDATA/node1/dev1", "ddata", 2);
        AddCanonical(store, "spBv1.0/plant1/DDATA/node1/dev2", "other-device", 3);
        AddCanonical(store, "spBv1.0/plant1/NDATA/node1", "node-data", 4);

        store.SelectTopics(DeviceScope("node1", "dev1"));
        var selected = store.GetSelectedTopicState();
        selected.MessageCount.Should().Be(2);
        Payloads(store, selected).Should().BeEquivalentTo(["dbirth", "ddata"]);

        AddCanonical(store, "spBv1.0/plant1/DCMD/node1/dev1", "dcmd", 5);

        var after = store.GetSelectedTopicState();
        after.Token.Generation.Should().Be(selected.Token.Generation);
        after.Token.ContentVersion.Should().BeGreaterThan(selected.Token.ContentVersion);
        after.MessageCount.Should().Be(3, "a verb that first appears after the click still belongs");
    }

    [Test]
    public void NodeScope_AdmitsAVerbThatFirstAppearsAfterSelection()
    {
        var store = CreateStore();
        AddCanonical(store, "spBv1.0/plant1/NDATA/node1", "node-data", 1);
        store.SelectTopics(NodeScope("node1"));
        var selected = store.GetSelectedTopicState();

        AddCanonical(store, "spBv1.0/plant1/NDEATH/node1", "death", 2);

        var after = store.GetSelectedTopicState();
        after.Token.Generation.Should().Be(selected.Token.Generation, "the row did not change");
        after.Token.ContentVersion.Should().BeGreaterThan(selected.Token.ContentVersion);
        after.Label.Should().Be(selected.Label, "a scoped name does not go stale as coverage grows");
        after.MessageCount.Should().Be(2);
        Payloads(store, selected).Should().BeEquivalentTo(["node-data", "death"]);
    }

    [Test]
    public void NodeScope_AdmitsADeviceThatFirstAppearsAfterSelection()
    {
        var store = CreateStore();
        AddCanonical(store, "spBv1.0/plant1/DDATA/node1/dev1", "dev1", 1);
        store.SelectTopics(NodeScope("node1"));
        var selected = store.GetSelectedTopicState();

        AddCanonical(store, "spBv1.0/plant1/DDATA/node1/dev2", "dev2", 2);

        var after = store.GetSelectedTopicState();
        after.Token.Generation.Should().Be(selected.Token.Generation);
        after.MessageCount.Should().Be(2, "the new device is under the selected node");
        Payloads(store, selected).Should().BeEquivalentTo(["dev1", "dev2"]);
    }

    [Test]
    public void DevicesScope_AdmitsANewDeviceButNotNodeTraffic()
    {
        var store = CreateStore();
        AddCanonical(store, "spBv1.0/plant1/DDATA/node1/dev1", "dev1", 1);
        store.SelectTopics(DevicesScope("node1"));
        var selected = store.GetSelectedTopicState();

        AddCanonical(store, "spBv1.0/plant1/DDATA/node1/dev2", "dev2", 2);
        AddCanonical(store, "spBv1.0/plant1/NDATA/node1", "node-data", 3);

        var after = store.GetSelectedTopicState();
        after.Token.Generation.Should().Be(selected.Token.Generation);
        after.MessageCount.Should().Be(2, "the node's own verb is not part of a devices selection");
        Payloads(store, selected).Should().BeEquivalentTo(["dev1", "dev2"]);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void PathSelection_NormalizesOverlapsRegardlessOfOrder(bool ancestorFirst)
    {
        var store = CreateStore();
        const string ancestor = "spBv1.0/plant1/DDATA/node1/dev1";
        const string descendant = "spBv1.0/plant1/DDATA/node1/dev1/deeper";
        AddCanonical(store, ancestor, "dev1", 1);
        AddCanonical(store, descendant, "deeper", 2);

        store.SelectTopics(new TopicSelection(
            "group", "group", ancestorFirst ? [ancestor, descendant] : [descendant, ancestor]));

        var state = store.GetSelectedTopicState();
        state.MessageCount.Should().Be(2, "the covered descendant is not counted twice");
        Payloads(store, state).Should().HaveCount(2);
    }

    [Test]
    public void PathSelection_IgnoresDuplicatePaths()
    {
        var store = CreateStore();
        const string topic = "spBv1.0/plant1/DDATA/node1/dev1";
        AddCanonical(store, topic, "dev1", 1);

        store.SelectTopics(new TopicSelection("group", "group", [topic, topic]));

        var state = store.GetSelectedTopicState();
        state.MessageCount.Should().Be(1);
        Payloads(store, state).Should().ContainSingle();
    }

    [Test]
    public void PathSelection_KeepsTheResolvablePartAndRefusesOneThatResolvesToNothing()
    {
        var store = CreateStore();
        AddCanonical(store, "spBv1.0/plant1/DDATA/node1/dev1", "dev1", 1);
        store.SelectTopic("spBv1.0/plant1/DDATA/node1/dev1");

        store.SelectTopics(new TopicSelection(
            "group", "group", ["spBv1.0/plant1/DDATA/node1/dev1", "spBv1.0/plant1/DDATA/node1/dev2"]));

        var partial = store.GetSelectedTopicState();
        partial.Key.Should().Be("group");
        partial.MessageCount.Should().Be(1);

        store.SelectTopics(new TopicSelection("gone", "gone", ["spBv1.0/plant1/DDATA/node1/dev2"]));

        store.GetSelectedTopicState().Key.Should().Be("group", "a stale row cannot clear a live selection");
    }

    [Test]
    public void ReorderingTheSameCoverageDoesNotMoveTheSelection()
    {
        var store = CreateStore();
        const string dev1 = "spBv1.0/plant1/DDATA/node1/dev1";
        const string dev2 = "spBv1.0/plant1/DDATA/node1/dev2";
        AddCanonical(store, dev1, "dev1", 1);
        AddCanonical(store, dev2, "dev2", 2);

        store.SelectTopics(new TopicSelection("group", "group", [dev1, dev2]));
        var first = store.GetSelectedTopicState();
        store.SelectTopics(new TopicSelection("group", "group", [dev2, dev1]));
        var repeated = store.GetSelectedTopicState();

        repeated.Token.Generation.Should().Be(first.Token.Generation, "the row is the same one");
        repeated.Token.ContentVersion.Should().Be(first.Token.ContentVersion);
    }

    [Test]
    public void ReselectingTheSamePathDoesNotMoveTheSelection()
    {
        var store = CreateStore();
        store.Add("spBv1.0/plant1/DDATA/node1/dev1", Message("dev1", 1, "spBv1.0/plant1/DDATA/node1/dev1"));
        var selection = new TopicSelection("group", "group", ["spBv1.0/plant1/DDATA/node1/dev1"]);

        store.SelectTopics(selection);
        var first = store.GetSelectedTopicState();
        store.SelectTopics(selection);
        var repeated = store.GetSelectedTopicState();

        repeated.Should().Be(first);
    }

    [Test]
    public void NodeScope_ExcludesAnEndpointReachedOnlyThroughAnAlias()
    {
        var store = CreateStore();
        store.Add("spBv1.0/plant1/NDATA/node1", Message("aliased", 1, "spBv1.0/plant1//NDATA/node1"));

        store.SelectTopics(NodeScope("node1"));

        store.GetSelectedTopicState().Key.Should().BeNull("the endpoint has no canonical spelling yet");

        AddCanonical(store, "spBv1.0/plant1/NDATA/node1", "canonical", 2);
        store.SelectTopics(NodeScope("node1"));

        var state = store.GetSelectedTopicState();
        state.Key.Should().Be(NodeKey);
        state.MessageCount.Should().Be(2);
    }

    [Test]
    public void DevicesScope_ExcludesAPrefixThatNeverHeldACanonicalMessage()
    {
        var store = CreateStore();
        AddCanonical(store, "spBv1.0/plant1/DDATA/node1/dev1/extra", "deeper", 1);

        store.SelectTopics(DevicesScope("node1"));

        store.GetSelectedTopicState().Key.Should().BeNull("dev1 is only a prefix of the observed topic");
    }

    [Test]
    public void PurgeRemovingASelectedEndpointClearsTheWholeSelection()
    {
        var store = CreateStore();
        AddCanonical(store, "spBv1.0/plant1/DDATA/node1/dev1", "dev1", 1);
        AddCanonical(store, "spBv1.0/plant1/DDATA/node1/dev2", "dev2", 2);
        store.SelectTopics(DevicesScope("node1"));
        var selected = store.GetSelectedTopicState();

        store.RemoveMatchingTopic("spBv1.0/plant1/DDATA/node1/dev1");

        store.GetSelectedTopicState().Key.Should().BeNull();
        store.GetSelectedMessages(selected.Token, 10).Should().BeNull();
    }

    [Test]
    public void PurgeRetiringTheCanonicalSpellingClearsTheSelectionEvenIfTheNodeSurvives()
    {
        var store = CreateStore();
        AddCanonical(store, "spBv1.0/plant1/NDATA/node1", "canonical", 1);
        AddCanonical(store, "spBv1.0/plant1/NDATA/node1/extra", "extra", 2);
        store.SelectTopics(NodeScope("node1"));
        store.GetSelectedTopicState().Key.Should().NotBeNull();

        // The endpoint's child keeps the node alive, but the spelling the projection grouped it by
        // is gone, so the row and the selection must agree that it is no longer selected.
        store.RemoveMatchingTopic("spBv1.0/plant1/NDATA/node1");

        store.GetSelectedTopicState().Key.Should().BeNull();
    }

    [Test]
    public void ClearDropsTheScopeAndLaterTrafficDoesNotResurrectIt()
    {
        var store = CreateStore();
        AddCanonical(store, "spBv1.0/plant1/NDATA/node1", "node-data", 1);
        store.SelectTopics(NodeScope("node1"));

        store.Clear();

        store.GetSelectedTopicState().Key.Should().BeNull();
        AddCanonical(store, "spBv1.0/plant1/DDATA/node1/dev9", "late", 2);
        var state = store.GetSelectedTopicState();
        state.Key.Should().BeNull();
        state.MessageCount.Should().Be(0);
    }

    [Test]
    public void SwitchingSelectionEndsTheOldScopeMembership()
    {
        var store = CreateStore();
        AddCanonical(store, "spBv1.0/plant1/NDATA/node1", "node-data", 1);
        store.SelectTopics(NodeScope("node1"));

        store.SelectTopic("spBv1.0/plant1/NDATA/node1");
        store.GetSelectedTopicState().Key.Should().Be("spBv1.0/plant1/NDATA/node1");

        AddCanonical(store, "spBv1.0/plant1/NBIRTH/node1", "birth", 2);

        store.GetSelectedTopicState().MessageCount.Should().Be(1, "the old scope must not adopt new traffic");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ConcurrentAddAndSelectionChangeLeaveNoResurrectedMembership(bool switchInsteadOfClear)
    {
        const int iterations = 50;
        var store = CreateStore();

        for (var i = 0; i < iterations; i++)
        {
            AddCanonical(store, "sensors/temp", "sensors", 0);
            AddCanonical(store, "spBv1.0/plant1/NDATA/node1", $"node-data-{i}", i + 1);
            store.SelectTopics(NodeScope("node1"));
            using var barrier = new Barrier(2);

            var add = Task.Run(() =>
            {
                if (!barrier.SignalAndWait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Add could not synchronize with the selection change.");
                AddCanonical(store, "spBv1.0/plant1/NBIRTH/node1", $"birth-{i}", 1000 + i);
            });
            var change = Task.Run(() =>
            {
                if (!barrier.SignalAndWait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("The selection change could not synchronize with Add.");
                if (switchInsteadOfClear)
                    store.SelectTopic("sensors/temp");
                else
                    store.SelectTopics(null);
            });

            await Task.WhenAll(add, change).WaitAsync(TimeSpan.FromSeconds(15));

            var state = store.GetSelectedTopicState();
            if (switchInsteadOfClear)
            {
                state.Key.Should().Be("sensors/temp");
                state.MessageCount.Should().Be(1, "the raced message belongs to a scope that was left behind");
            }
            else
            {
                state.Key.Should().BeNull();
                state.MessageCount.Should().Be(0, "a cleared scope must not adopt a message that raced the clear");
            }

            store.Clear();
        }
    }

    [Test]
    public void TryClearSelection_RejectsAStaleKeyAndAcceptsTheCurrentOne()
    {
        var store = CreateStore();
        AddCanonical(store, "spBv1.0/plant1/DDATA/node1/dev1", "dev1", 1);
        store.SelectTopics(DevicesScope("node1"));
        var current = store.GetSelectedTopicState();

        store.TryClearSelection(store.Version, current with { Key = "some-other-row" })
            .Should().BeFalse();
        store.GetSelectedTopicState().Key.Should().Be(DevicesKey);

        store.TryClearSelection(store.Version, current).Should().BeTrue();
        store.GetSelectedTopicState().Key.Should().BeNull();
    }

    private static string NodeKey => $"\0spb:node:{GroupPath}/node1";

    private static string DevicesKey => $"\0spb:dev:{GroupPath}/node1";

    private static TopicSelection NodeScope(string nodeId) =>
        new($"\0spb:node:{GroupPath}/{nodeId}", $"{GroupPath}/{nodeId}", [],
            new SparkplugSelectionScope(SparkplugSelectionKind.Node, GroupPath, nodeId));

    private static TopicSelection DevicesScope(string nodeId) =>
        new($"\0spb:dev:{GroupPath}/{nodeId}", $"{GroupPath}/{nodeId}/Devices", [],
            new SparkplugSelectionScope(SparkplugSelectionKind.Devices, GroupPath, nodeId));

    private static TopicSelection DeviceScope(string nodeId, string deviceId) =>
        new($"\0spb:device:{GroupPath}/{nodeId}/{deviceId}", $"{GroupPath}/{nodeId}/{deviceId}", [],
            new SparkplugSelectionScope(SparkplugSelectionKind.Device, GroupPath, nodeId, deviceId));

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
