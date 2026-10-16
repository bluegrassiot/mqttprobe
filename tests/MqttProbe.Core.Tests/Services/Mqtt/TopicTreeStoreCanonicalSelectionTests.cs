using Microsoft.Extensions.Logging;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Mqtt;

namespace MqttProbe.Core.Tests.Services.Mqtt;

[TestFixture]
public sealed class TopicTreeStoreCanonicalSelectionTests
{
    private const string NodeEndpoint = "spBv1.0/plant1/NBIRTH/node1";
    private const string DeviceEndpoint = "spBv1.0/plant1/DBIRTH/node1/dev1";

    [TestCase(NodeEndpoint)]
    [TestCase(DeviceEndpoint)]
    public void PurgedEndpointWithASurvivingDescendant_ClearsTheOldToken(string endpoint)
    {
        var store = CreateStore();
        AddCanonical(store, endpoint, "value", 1);
        AddCanonical(store, endpoint + "/extra", "extra", 2);
        store.SelectTopics(Canonical(endpoint));
        var selected = store.GetSelectedTopicState();
        selected.Key.Should().Be(endpoint);

        store.RemoveMatchingTopic(endpoint);

        var after = store.GetSelectedTopicState();
        after.Key.Should().BeNull();
        store.GetSelectedMessages(selected.Token, 10).Should().BeNull();
    }

    [TestCase(NodeEndpoint)]
    [TestCase(DeviceEndpoint)]
    public void FreshCanonicalTrafficBeforeTheUiRefreshes_LeavesTheClearedSelectionCleared(string endpoint)
    {
        var store = CreateStore();
        AddCanonical(store, endpoint, "value", 1);
        AddCanonical(store, endpoint + "/extra", "extra", 2);
        store.SelectTopics(Canonical(endpoint));

        store.RemoveMatchingTopic(endpoint);
        AddCanonical(store, endpoint, "fresh", 3);

        store.GetSelectedTopicState().Key.Should().BeNull("the purge cleared the selection before traffic returned");
    }

    [TestCase(NodeEndpoint)]
    [TestCase(DeviceEndpoint)]
    public void AfterAPurge_AnotherSelectionAndAStaleReplay_LeaveTheStateExactlyUnchanged(string endpoint)
    {
        var store = CreateStore();
        AddCanonical(store, endpoint, "value", 1);
        AddCanonical(store, endpoint + "/extra", "extra", 2);
        store.SelectTopics(Canonical(endpoint));
        store.RemoveMatchingTopic(endpoint);

        AddCanonical(store, "sensors/temp", "sensors", 3);
        store.SelectTopic("sensors/temp");
        var live = store.GetSelectedTopicState();

        store.SelectTopics(Canonical(endpoint));
        store.GetSelectedTopicState().Should().Be(live, "a stale row cannot replace a live selection");

        store.TryClearSelection(store.Version, live with { Key = endpoint }).Should().BeFalse();
        store.GetSelectedTopicState().Should().Be(live);
    }

    [TestCase(NodeEndpoint)]
    [TestCase(DeviceEndpoint)]
    public void AliasOnlyOrStructuralPrefixEndpoint_IsRefused(string endpoint)
    {
        var aliasOnly = CreateStore();
        AddCanonical(aliasOnly, "sensors/temp", "sensors", 1);
        aliasOnly.SelectTopic("sensors/temp");
        var configured = aliasOnly.GetSelectedTopicState();
        aliasOnly.Add(endpoint, Message("aliased", 2, Alias(endpoint)));
        aliasOnly.SelectTopics(Canonical(endpoint));
        aliasOnly.GetSelectedTopicState().Should().Be(configured);

        var structuralPrefix = CreateStore();
        AddCanonical(structuralPrefix, "sensors/temp", "sensors", 1);
        structuralPrefix.SelectTopic("sensors/temp");
        var prefixState = structuralPrefix.GetSelectedTopicState();
        AddCanonical(structuralPrefix, endpoint + "/extra", "extra", 2);
        structuralPrefix.SelectTopics(Canonical(endpoint));
        structuralPrefix.GetSelectedTopicState().Should().Be(prefixState);
    }

    [Test]
    public void ARequestWithOneNonCanonicalTopic_IsRefusedEntirely()
    {
        var store = CreateStore();
        AddCanonical(store, NodeEndpoint, "node", 1);
        AddCanonical(store, "sensors/temp", "sensors", 2);
        store.SelectTopic("sensors/temp");
        var live = store.GetSelectedTopicState();

        store.Add("spBv1.0/plant1/NBIRTH/node2", Message("aliased", 3, "spBv1.0/plant1//NBIRTH/node2"));

        store.SelectTopics(new TopicSelection(
            "group", "group", [NodeEndpoint, "spBv1.0/plant1/NBIRTH/node2"], RequireCanonicalTopics: true));

        store.GetSelectedTopicState().Should().Be(live);
    }

    [TestCase(NodeEndpoint)]
    [TestCase(DeviceEndpoint)]
    public void UnconstrainedLiteralSelection_KeepsItsSubtreeAcrossAPurge(string endpoint)
    {
        var store = CreateStore();
        AddCanonical(store, endpoint, "value", 1);
        AddCanonical(store, endpoint + "/extra", "extra", 2);
        store.SelectTopic(endpoint);
        var selected = store.GetSelectedTopicState();

        store.RemoveMatchingTopic(endpoint);

        var after = store.GetSelectedTopicState();
        after.Key.Should().Be(endpoint, "a literal selection is structural, not canonical");
        after.Token.Generation.Should().Be(selected.Token.Generation);
        store.GetSelectedMessages(selected.Token, 10)!.Messages
            .Select(message => message.Payload).Should().BeEquivalentTo(["extra"]);
    }

    [TestCase(NodeEndpoint)]
    [TestCase(DeviceEndpoint)]
    public void CanonicalRequirement_IsPartOfTheSelectionIdentity(string endpoint)
    {
        var store = CreateStore();
        AddCanonical(store, endpoint, "value", 1);
        var unconstrained = new TopicSelection(endpoint, endpoint, [endpoint]);
        var constrained = unconstrained with { RequireCanonicalTopics = true };

        store.SelectTopics(unconstrained);
        var first = store.GetSelectedTopicState();

        store.SelectTopics(unconstrained);
        store.GetSelectedTopicState().Token.Generation.Should().Be(first.Token.Generation);

        store.SelectTopics(constrained);
        var required = store.GetSelectedTopicState();
        required.Token.Generation.Should().Be(first.Token.Generation + 1);
        required.Token.ContentVersion.Should().Be(first.Token.ContentVersion + 1);

        store.SelectTopics(unconstrained);
        store.GetSelectedTopicState().Token.Generation.Should().Be(required.Token.Generation + 1);
    }

    [TestCase(NodeEndpoint)]
    [TestCase(DeviceEndpoint)]
    public void ConstrainedRequest_WithACanonicalAndAMissingTopic_IsRefusedInEitherOrder(string endpoint)
    {
        var store = CreateStore();
        AddCanonical(store, endpoint, "value", 1);
        AddCanonical(store, "sensors/temp", "sensors", 2);
        store.SelectTopic("sensors/temp");
        var live = store.GetSelectedTopicState();
        var missing = endpoint + "/missing";

        store.SelectTopics(CanonicalRequest(endpoint, missing));
        store.GetSelectedTopicState().Should().Be(live, "a requested topic that does not resolve refuses the request");

        store.SelectTopics(CanonicalRequest(missing, endpoint));
        store.GetSelectedTopicState().Should().Be(live);
    }

    [TestCase(NodeEndpoint)]
    [TestCase(DeviceEndpoint)]
    public void ConstrainedRequest_WithACanonicalAncestorAndANonCanonicalDescendant_IsRefusedInEitherOrder(string endpoint)
    {
        var store = CreateStore();
        AddCanonical(store, endpoint, "value", 1);
        var descendant = endpoint + "/extra";
        store.Add(descendant, Message("aliased", 2, Alias(descendant)));
        AddCanonical(store, "sensors/temp", "sensors", 3);
        store.SelectTopic("sensors/temp");
        var live = store.GetSelectedTopicState();

        store.SelectTopics(CanonicalRequest(endpoint, descendant));
        store.GetSelectedTopicState().Should().Be(live,
            "a covered descendant is still a requested topic and must be checked");

        store.SelectTopics(CanonicalRequest(descendant, endpoint));
        store.GetSelectedTopicState().Should().Be(live);
    }

    [Test]
    public void ConstrainedRequest_WithAnInvalidPath_IsRefusedInEitherOrder()
    {
        var store = CreateStore();
        AddCanonical(store, NodeEndpoint, "node", 1);
        AddCanonical(store, "sensors/temp", "sensors", 2);
        store.SelectTopic("sensors/temp");
        var live = store.GetSelectedTopicState();

        store.SelectTopics(CanonicalRequest(NodeEndpoint, "///"));
        store.GetSelectedTopicState().Should().Be(live);

        store.SelectTopics(CanonicalRequest("///", NodeEndpoint));
        store.GetSelectedTopicState().Should().Be(live);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void ConstrainedRequest_WithACanonicalOverlap_Deduplicates(bool ancestorFirst)
    {
        var store = CreateStore();
        const string ancestor = "spBv1.0/plant1/NBIRTH/node1";
        const string descendant = "spBv1.0/plant1/NBIRTH/node1/extra";
        AddCanonical(store, ancestor, "node", 1);
        AddCanonical(store, descendant, "extra", 2);

        store.SelectTopics(CanonicalRequest(
            ancestorFirst ? [ancestor, descendant] : [descendant, ancestor]));

        var state = store.GetSelectedTopicState();
        state.MessageCount.Should().Be(2, "the covered descendant is not counted twice");
        store.GetSelectedMessages(state.Token, 10)!.Messages.Should().HaveCount(2);
    }

    [Test]
    public void ScopedRequest_ResolvesTheCurrentCoverageAndIgnoresStaleTopics()
    {
        var store = CreateStore();
        AddCanonical(store, "spBv1.0/plant1/NDATA/node1", "node", 1);
        AddCanonical(store, "spBv1.0/plant1/DDATA/node1/dev1", "dev1", 2);

        store.SelectTopics(new TopicSelection(
            "\0spb:node:spBv1.0/plant1/node1", "spBv1.0/plant1/node1",
            ["spBv1.0/plant1/gone", "spBv1.0/plant1//NDATA/node1"],
            new SparkplugSelectionScope(SparkplugSelectionKind.Node, "spBv1.0/plant1", "node1")));

        var state = store.GetSelectedTopicState();
        state.MessageCount.Should().Be(2, "a scope is resolved from the store, not the click-time topics");
    }

    [Test]
    public void UnconstrainedRequest_KeepsTheResolvablePartAndDropsTheMissing()
    {
        var store = CreateStore();
        AddCanonical(store, NodeEndpoint, "node", 1);

        store.SelectTopics(new TopicSelection("group", "group", [NodeEndpoint, "spBv1.0/plant1/gone"]));

        var state = store.GetSelectedTopicState();
        state.Key.Should().Be("group");
        state.MessageCount.Should().Be(1);
    }

    private static TopicSelection Canonical(string topic) =>
        new(topic, topic, [topic], RequireCanonicalTopics: true);

    private static TopicSelection CanonicalRequest(params string[] topics) =>
        new(string.Join('|', topics), string.Join('|', topics), [.. topics], RequireCanonicalTopics: true);

    private static string Alias(string endpoint) => endpoint.Replace("/plant1/", "/plant1//", StringComparison.Ordinal);

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
