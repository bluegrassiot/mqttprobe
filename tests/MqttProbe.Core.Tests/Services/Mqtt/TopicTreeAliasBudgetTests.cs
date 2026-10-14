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
    public void AliasSpray_NormalizingOntoTwoNodes_PlateausEntriesAndCharactersWithOneRetainedMessage()
    {
        var store = CreateStore(maxMessages: 1, maxTopicNodes: 2);

        store.Add("root//tip", Message("a", 1, "root//tip"));
        store.Add("root///tip", Message("b", 2, "root///tip"));

        var plateauCharacters = "root//tip".Length + "root///tip".Length;
        store.TopicNodeCount.Should().Be(2, "root and root/tip");
        store.TotalStoredMessages.Should().Be(1, "retention keeps one message");
        store.AliasEntryCount.Should().Be(2, "the alias entry budget is tied to the node budget");
        store.AliasCharacterCount.Should().Be(plateauCharacters);

        store.Add("root/tip/", Message("c", 3, "root/tip/"));
        store.Add("/root/tip", Message("d", 4, "/root/tip"));
        store.Add("root/tip//", Message("e", 5, "root/tip//"));

        store.TopicNodeCount.Should().Be(2);
        store.TotalStoredMessages.Should().Be(1);
        store.AliasEntryCount.Should().Be(2, "a saturated budget admits no new spellings");
        store.AliasCharacterCount.Should().Be(plateauCharacters, "rejected spellings add no characters");
    }

    [Test]
    public void KnownAliasSpelling_RemainsAdmissibleWhenTheBudgetIsSaturated()
    {
        var store = CreateStore(maxMessages: 100, maxTopicNodes: 2);
        store.Add("root//tip", Message("a", 1, "root//tip"));
        store.Add("root///tip", Message("b", 2, "root///tip"));
        store.AliasEntryCount.Should().Be(2);

        store.Add("root//tip", Message("c", 3, "root//tip"));

        store.TotalStoredMessages.Should().Be(3);
        store.AliasEntryCount.Should().Be(2, "a known spelling consumes no new entry");
    }

    [Test]
    public void CanonicalPublication_RemainsAdmissibleWhenSaturatedAndSurvivesEviction()
    {
        var store = CreateStore(maxMessages: 2, maxTopicNodes: 2);
        store.Add("root//tip", Message("alias-a", 1, "root//tip"));
        store.Add("root///tip", Message("alias-b", 2, "root///tip"));
        store.Add("root/tip/", Message("rejected", 3, "root/tip/"));
        store.AliasEntryCount.Should().Be(2);

        store.Add("root/tip", Message("canonical-a", 4, "root/tip"));
        store.Add("root/tip", Message("canonical-b", 5, "root/tip"));

        store.TotalStoredMessages.Should().Be(2, "both retained messages are the canonical pair");
        var tip = store.GetTopicTreeSnapshot().Roots.Single(node => node.FullTopic == "root").Children.Single();
        tip.FullTopic.Should().Be("root/tip");
        tip.HasDirectMessages.Should().BeTrue();
        tip.HasObservedDirectTopic.Should().BeTrue();
        tip.HasObservedCanonicalTopic.Should().BeTrue(
            "the canonical spelling is never budgeted and its history outlives eviction");
    }

    [Test]
    public void RejectedAlias_AddsNoMessageObservationOrTreeChange()
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

        store.Add("root/tip/", Message("rejected", 3, "root/tip/"));

        store.TotalStoredMessages.Should().Be(messages);
        store.TopicNodeCount.Should().Be(nodes, "capacity is checked before any node is created");
        store.AliasEntryCount.Should().Be(entries);
        store.AliasCharacterCount.Should().Be(characters);
        store.Version.Should().Be(version, "a rejected alias is not a store change");
        store.GetTopicTreeSnapshot().Should().BeSameAs(snapshot, "the cached tree is untouched");
    }

    [Test]
    public void ExactExclusion_ReleasesOnlyTheMatchingAliasCapacity()
    {
        var store = CreateStore(maxMessages: 100, maxTopicNodes: 2);
        store.Add("root//tip", Message("a", 1, "root//tip"));
        store.Add("root///tip", Message("b", 2, "root///tip"));
        store.Add("root/tip/", Message("rejected", 3, "root/tip/"));
        store.TotalStoredMessages.Should().Be(2);

        store.RemoveMatchingTopic("root//tip");

        store.AliasEntryCount.Should().Be(1, "only the exact matching spelling retired");
        store.TotalStoredMessages.Should().Be(1, "the matching alias message was removed");

        store.Add("root/tip/", Message("accepted", 4, "root/tip/"));
        store.AliasEntryCount.Should().Be(2);

        store.Add("/root/tip", Message("rejected-again", 5, "/root/tip"));
        store.TotalStoredMessages.Should().Be(2, "the freed slot was consumed, so the store is saturated again");
        store.AliasEntryCount.Should().Be(2);
    }

    [Test]
    public void WildcardExclusion_ReleasesOnlyTheMatchingAliasCapacity()
    {
        var store = CreateStore(maxMessages: 100, maxTopicNodes: 4);
        store.Add("root//tip", Message("a", 1, "root//tip"));
        store.Add("/root/tip", Message("b", 2, "/root/tip"));
        store.Add("other//tip", Message("c", 3, "other//tip"));
        store.Add("/other/tip", Message("d", 4, "/other/tip"));
        store.Add("root/tip/", Message("rejected", 5, "root/tip/"));

        store.TopicNodeCount.Should().Be(4, "root, root/tip, other and other/tip");
        store.AliasEntryCount.Should().Be(4);
        store.TotalStoredMessages.Should().Be(4);

        store.RemoveMatchingTopic("root//+");

        store.AliasEntryCount.Should().Be(3, "the wildcard matched only the root double-slash alias");
        store.TotalStoredMessages.Should().Be(3);

        store.Add("root/tip/", Message("accepted", 6, "root/tip/"));
        store.TotalStoredMessages.Should().Be(4);

        store.Add("root///tip", Message("rejected-again", 7, "root///tip"));
        store.TotalStoredMessages.Should().Be(4, "one slot freed, one consumed");
        store.AliasEntryCount.Should().Be(4);
    }

    [Test]
    public void ClearResetsAliasCapacity()
    {
        var store = CreateStore(maxMessages: 100, maxTopicNodes: 2);
        store.Add("root//tip", Message("a", 1, "root//tip"));
        store.Add("root///tip", Message("b", 2, "root///tip"));
        store.AliasEntryCount.Should().Be(2);

        store.Clear();

        store.AliasEntryCount.Should().Be(0);
        store.AliasCharacterCount.Should().Be(0);

        store.Add("root/tip/", Message("fresh", 3, "root/tip/"));

        store.TotalStoredMessages.Should().Be(1);
        store.AliasEntryCount.Should().Be(1, "the budget refills after a clear");
    }

    [Test]
    public void AliasLongerThanTheCharacterBudget_IsRejectedBeforeAnyNodeIsCreated()
    {
        var store = CreateStore(maxMessages: 100, maxTopicNodes: 100);
        var raw = "root//" + new string('a', AliasCharacterBudget);

        store.Add(raw, Message("huge", 1, raw));

        store.TopicNodeCount.Should().Be(0, "capacity is checked before nodes are created");
        store.TotalStoredMessages.Should().Be(0);
        store.AliasEntryCount.Should().Be(0);
        store.AliasCharacterCount.Should().Be(0);
    }

    [Test]
    public void AliasCharacters_PlateauAtTheAggregateBudget()
    {
        var store = CreateStore(maxMessages: 100, maxTopicNodes: 100);
        var first = "root//" + new string('a', 3 * 1024 * 1024);
        var second = "/root//" + new string('b', 3 * 1024 * 1024);

        store.Add(first, Message("first", 1, first));
        store.Add(second, Message("second", 2, second));

        store.AliasEntryCount.Should().Be(1);
        store.AliasCharacterCount.Should().Be(first.Length);
        store.TotalStoredMessages.Should().Be(1, "the second spelling exceeded the remaining characters");
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
    public void NullTopicFallbackAndSamePathAliases_RemainAdmissible()
    {
        var store = CreateStore();
        store.Add("root/child", new MqttMessage { Payload = "topic-less" });
        store.Add("root//child", Message("alias", 2, "root//child"));

        store.TotalStoredMessages.Should().Be(2);
        var child = store.GetTopicTreeSnapshot().Roots.Single().Children.Single();
        child.FullTopic.Should().Be("root/child");
        child.HasObservedDirectTopic.Should().BeTrue();
        child.HasObservedCanonicalTopic.Should().BeTrue("the topic-less fallback records the node path");
    }

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
