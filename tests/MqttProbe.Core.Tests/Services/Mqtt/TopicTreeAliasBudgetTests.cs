using Microsoft.Extensions.Logging;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Mqtt;

namespace MqttProbe.Core.Tests.Services.Mqtt;

[TestFixture]
public sealed class TopicTreeAliasBudgetTests
{
    private const int AliasCharacterBudget = 4 * 1024 * 1024;

    [Test]
    public void RawSpray_StaysOnDistinctNodesAndKeepsOneRetainedMessage()
    {
        var store = CreateStore(maxMessages: 1, maxTopicNodes: 100);

        store.Add("root//tip", Message("a", 1, "root//tip"));
        store.Add("root///tip", Message("b", 2, "root///tip"));

        store.TotalStoredMessages.Should().Be(1, "retention keeps one message");
        MessagePaths(store).Should().ContainSingle().Which.Should()
            .Be("root///tip", "the newest raw spelling holds the retained message");
        store.AliasEntryCount.Should().Be(0, "a raw spelling is not alias accounting");
        store.AliasCharacterCount.Should().Be(0);

        store.Add("root/tip/", Message("c", 3, "root/tip/"));
        store.Add("/root/tip", Message("d", 4, "/root/tip"));
        store.Add("root/tip//", Message("e", 5, "root/tip//"));

        store.TotalStoredMessages.Should().Be(1);
        MessagePaths(store).Should().Equal("root/tip//");
        store.AliasEntryCount.Should().Be(0);
        store.AliasCharacterCount.Should().Be(0);
    }

    [Test]
    public void RepeatedRawSpelling_AddsAnotherMessageToTheSameNode()
    {
        var store = CreateStore(maxMessages: 100, maxTopicNodes: 100);
        store.Add("root//tip", Message("a", 1, "root//tip"));
        store.Add("root///tip", Message("b", 2, "root///tip"));

        store.Add("root//tip", Message("c", 3, "root//tip"));

        store.TotalStoredMessages.Should().Be(3);
        MessagePaths(store).Should().BeEquivalentTo(["root//tip", "root///tip"]);
        Node(store, "root//tip").MessageCount.Should().Be(2, "a repeated raw spelling is the same node");
        Node(store, "root///tip").MessageCount.Should().Be(1);
        store.AliasEntryCount.Should().Be(0);
    }

    [Test]
    public void DistinctRawSpellings_KeepSeparateHistoryAcrossEviction()
    {
        var store = CreateStore(maxMessages: 2, maxTopicNodes: 100);
        store.Add("root//tip", Message("alias-a", 1, "root//tip"));
        store.Add("root///tip", Message("alias-b", 2, "root///tip"));
        store.Add("root/tip", Message("canonical-a", 3, "root/tip"));
        store.Add("root/tip", Message("canonical-b", 4, "root/tip"));

        store.TotalStoredMessages.Should().Be(2, "the retained pair is the canonical spelling");
        var tip = Node(store, "root/tip");
        tip.HasDirectMessages.Should().BeTrue();
        tip.MessageCount.Should().Be(2);

        Node(store, "root//tip").HasObservedDirectTopic.Should().BeTrue(
            "eviction drops the message, not the raw spelling it arrived under");
        Node(store, "root///tip").HasObservedDirectTopic.Should().BeTrue();
        store.AliasEntryCount.Should().Be(0);
    }

    [Test]
    public void NodeBudget_RefusesANewPathWithoutTouchingTheStore()
    {
        var store = CreateStore(maxMessages: 100, maxTopicNodes: 2);
        store.Add("root//tip", Message("a", 1, "root//tip"));
        store.Add("root///tip", Message("b", 2, "root///tip"));

        var snapshot = store.GetTopicTreeSnapshot();
        var version = store.Version;
        var messages = store.TotalStoredMessages;
        var nodes = store.TopicNodeCount;
        var entries = store.AliasEntryCount;
        var characters = store.AliasCharacterCount;

        store.Add("root/tip/", Message("refused", 3, "root/tip/"));

        store.TotalStoredMessages.Should().Be(messages);
        store.TopicNodeCount.Should().Be(nodes, "capacity is checked before any node is created");
        store.AliasEntryCount.Should().Be(entries);
        store.AliasCharacterCount.Should().Be(characters);
        store.Version.Should().Be(version, "a refused path is not a store change");
        store.GetTopicTreeSnapshot().Should().BeSameAs(snapshot, "the cached tree is untouched");
    }

    [Test]
    public void ExactExclusion_RemovesOnlyTheMatchingRawPath()
    {
        var store = CreateStore(maxMessages: 100, maxTopicNodes: 100);
        store.Add("root//tip", Message("a", 1, "root//tip"));
        store.Add("root///tip", Message("b", 2, "root///tip"));

        store.RemoveMatchingTopic("root//tip");

        store.TotalStoredMessages.Should().Be(1, "only the exact matching raw path retired");
        MessagePaths(store).Should().Equal("root///tip");
        AllNodes(store).Should().NotContain(node => node.FullTopic == "root//tip");

        store.Add("root/tip/", Message("accepted", 3, "root/tip/"));

        store.TotalStoredMessages.Should().Be(2);
        MessagePaths(store).Should().BeEquivalentTo(["root///tip", "root/tip/"]);
    }

    [Test]
    public void WildcardExclusion_RemovesOnlyTheMatchingRawPaths()
    {
        var store = CreateStore(maxMessages: 100, maxTopicNodes: 100);
        store.Add("root//tip", Message("a", 1, "root//tip"));
        store.Add("/root/tip", Message("b", 2, "/root/tip"));
        store.Add("other//tip", Message("c", 3, "other//tip"));
        store.Add("/other/tip", Message("d", 4, "/other/tip"));

        store.TotalStoredMessages.Should().Be(4);

        store.RemoveMatchingTopic("root//+");

        store.TotalStoredMessages.Should().Be(3);
        MessagePaths(store).Should().NotContain("root//tip");
        MessagePaths(store).Should().BeEquivalentTo(["/root/tip", "other//tip", "/other/tip"]);

        store.Add("root/tip/", Message("accepted", 5, "root/tip/"));

        store.TotalStoredMessages.Should().Be(4);
        MessagePaths(store).Should().Contain("root/tip/");
    }

    [Test]
    public void ClearResetsEveryRawPath()
    {
        var store = CreateStore(maxMessages: 100, maxTopicNodes: 100);
        store.Add("root//tip", Message("a", 1, "root//tip"));
        store.Add("root///tip", Message("b", 2, "root///tip"));
        store.TotalStoredMessages.Should().Be(2);

        store.Clear();

        store.TotalStoredMessages.Should().Be(0);
        store.TopicNodeCount.Should().Be(0);
        store.AliasEntryCount.Should().Be(0);
        store.AliasCharacterCount.Should().Be(0);

        store.Add("root/tip/", Message("fresh", 3, "root/tip/"));

        store.TotalStoredMessages.Should().Be(1);
        MessagePaths(store).Should().Equal("root/tip/");
    }

    [Test]
    public void OversizedRawPath_IsStoredWithoutAliasAccounting()
    {
        var store = CreateStore(maxMessages: 100, maxTopicNodes: 100);
        var raw = "root//" + new string('a', AliasCharacterBudget);

        store.Add(raw, Message("huge", 1, raw));

        store.TotalStoredMessages.Should().Be(1, "a long raw path is a path, not a budgeted alias");
        store.TopicNodeCount.Should().Be(3, "root, the empty level and the long leaf");
        store.AliasEntryCount.Should().Be(0);
        store.AliasCharacterCount.Should().Be(0);
    }

    [Test]
    public void RawSpellings_DoNotShareACharacterBudget()
    {
        var store = CreateStore(maxMessages: 100, maxTopicNodes: 100);
        var first = "root//" + new string('a', 3 * 1024 * 1024);
        var second = "/root//" + new string('b', 3 * 1024 * 1024);

        store.Add(first, Message("first", 1, first));
        store.Add(second, Message("second", 2, second));

        store.TotalStoredMessages.Should().Be(2, "both raw paths are admitted");
        store.AliasEntryCount.Should().Be(0);
        store.AliasCharacterCount.Should().Be(0);
        MessagePaths(store).Should().HaveCount(2);
    }

    [Test]
    public void MessageTopicResolvingToADifferentNode_IsRejectedWithoutTouchingTheStore()
    {
        var store = CreateStore();
        store.Add("root/child", Message("mismatch", 1, "other/topic"));

        store.TopicNodeCount.Should().Be(0);
        store.TotalStoredMessages.Should().Be(0);
        store.AliasEntryCount.Should().Be(0);
        store.AliasCharacterCount.Should().Be(0);
        store.GetTopicTreeSnapshot().Roots.Should().BeEmpty();
    }

    [Test]
    public void EqualRawSpellingsTargetingDifferentNodes_AreRejectedWithoutRetainingEitherCopy()
    {
        var store = CreateStore();
        var first = new string('x', 4096);
        var second = new string('x', 4096);
        first.Should().NotBeSameAs(second);

        var version = store.Version;
        var snapshot = store.GetTopicTreeSnapshot();

        store.Add("node/one", Message("first", 1, first));
        store.Add("node/two", Message("second", 2, second));

        store.TopicNodeCount.Should().Be(0);
        store.TotalStoredMessages.Should().Be(0);
        store.AliasEntryCount.Should().Be(0);
        store.AliasCharacterCount.Should().Be(0);
        store.Version.Should().Be(version);
        store.GetTopicTreeSnapshot().Should().BeSameAs(snapshot);
    }

    [Test]
    public void NullTopicFallbackAndDistinctRawPaths_RemainAdmissible()
    {
        var store = CreateStore();
        store.Add("root/child", new MqttMessage { Payload = "topic-less" });
        store.Add("root//child", Message("slashed", 2, "root//child"));

        store.TotalStoredMessages.Should().Be(2);
        Node(store, "root/child").HasObservedCanonicalTopic.Should().BeTrue(
            "the topic-less fallback records the node path");
        Node(store, "root//child").FullTopic.Should().Be("root//child");
        MessagePaths(store).Should().BeEquivalentTo(["root/child", "root//child"]);
        store.AliasEntryCount.Should().Be(0);
    }

    private static IEnumerable<TopicNodeSnapshot> AllNodes(TopicTreeStore store) =>
        store.GetTopicTreeSnapshot().Roots.SelectMany(Flatten);

    private static IEnumerable<TopicNodeSnapshot> Flatten(TopicNodeSnapshot node) =>
        node.Children.SelectMany(Flatten).Prepend(node);

    private static IEnumerable<string> MessagePaths(TopicTreeStore store) =>
        AllNodes(store).Where(node => node.HasDirectMessages).Select(node => node.FullTopic);

    private static TopicNodeSnapshot Node(TopicTreeStore store, string fullTopic) =>
        AllNodes(store).Single(node => node.FullTopic == fullTopic);

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

    private static MqttMessage Message(string payload, int second, string topic) =>
        new(payload, topic, false, MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce)
        {
            DateTimeReceived = DateTime.UnixEpoch.AddSeconds(second)
        };
}
