using Microsoft.Extensions.Logging;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Mqtt;

namespace MqttProbe.Core.Tests.Services.Mqtt;

[TestFixture]
public sealed class TopicTreeObservedHistoryPruningTests
{
    [Test]
    public void ChildlessEvictedRawAlias_CanonicalExcludeKeepsTheUnmatchedHistory()
    {
        var store = CreateStore(maxMessages: 1);
        store.Add("root//tip", Message("alias", 1, "root//tip"));
        store.Add("other", Message("other", 2, "other"));

        var before = store.GetTopicTreeSnapshot();
        var tip = before.Roots.Single(node => node.FullTopic == "root").Children.Single();
        tip.HasDirectMessages.Should().BeFalse("the alias message was evicted");
        tip.HasObservedDirectTopic.Should().BeTrue();

        store.RemoveMatchingTopic("root/tip");
        var after = store.GetTopicTreeSnapshot();

        after.Should().NotBeSameAs(before, "the purge invalidated the cached snapshot");
        after.Version.Should().BeGreaterThan(before.Version);

        var kept = after.Roots.Single(node => node.FullTopic == "root").Children.Single();
        kept.FullTopic.Should().Be("root/tip");
        kept.HasDirectMessages.Should().BeFalse();
        kept.HasObservedDirectTopic.Should().BeTrue(
            "the canonical filter matched the path, but not the recorded raw alias");
    }

    [Test]
    public void ChildlessEvictedCanonicalHistory_AliasExcludeKeepsTheUnmatchedHistory()
    {
        var store = CreateStore(maxMessages: 1);
        store.Add("root/tip", Message("canonical", 1, "root/tip"));
        store.Add("other", Message("other", 2, "other"));

        store.RemoveMatchingTopic("root//tip");

        var tip = store.GetTopicTreeSnapshot().Roots.Single(node => node.FullTopic == "root")
            .Children.Single();
        tip.FullTopic.Should().Be("root/tip");
        tip.HasObservedDirectTopic.Should().BeTrue(
            "the raw alias filter did not match the recorded canonical name");
        store.TotalStoredMessages.Should().Be(1);
    }

    [Test]
    public void ChildlessEvictedRawAlias_ExactAliasExcludeRetiresHistoryAndPrunesTheNode()
    {
        var store = CreateStore(maxMessages: 1);
        store.Add("root//tip", Message("alias", 1, "root//tip"));
        store.Add("other", Message("other", 2, "other"));

        store.RemoveMatchingTopic("root//tip");

        store.GetTopicTreeSnapshot().Roots.Should().NotContain(node => node.FullTopic == "root",
            "the last recorded raw name retired, so the emptied ancestry is pruned");
        store.TopicNodeCount.Should().Be(1);
    }

    [Test]
    public void StructuralPrefixWithNoHistory_IsStillPruned()
    {
        var store = CreateStore();
        store.Add("root/child", Message("child", 1, "root/child"));
        store.Add("root/tip", Message("kept", 2, "root/tip"));

        store.RemoveMatchingTopic("root/child");

        var root = store.GetTopicTreeSnapshot().Roots.Single();
        root.FullTopic.Should().Be("root");
        root.HasObservedDirectTopic.Should().BeFalse("the prefix was never published to directly");
        root.Children.Should().ContainSingle(node => node.FullTopic == "root/tip",
            "the node holding the only remaining content survives");
        store.TopicNodeCount.Should().Be(2);
    }

    [Test]
    public void PurgingTheOnlyHistoryBeneathAStructuralPrefixPrunesBoth()
    {
        var store = CreateStore();
        store.Add("root/child", Message("child", 1, "root/child"));

        store.RemoveMatchingTopic("root/child");

        store.GetTopicTreeSnapshot().Roots.Should().BeEmpty(
            "the bare prefix held nothing of its own once its last child was pruned");
        store.TopicNodeCount.Should().Be(0);
        store.TotalStoredMessages.Should().Be(0);
    }

    [Test]
    public void PurgingTheLastMatchingHistory_PrunesTheEmptyAncestry()
    {
        var store = CreateStore(maxMessages: 1);
        store.Add("root//tip", Message("alias", 1, "root//tip"));
        store.Add("root/tip", Message("canonical", 2, "root/tip"));
        store.Add("other", Message("other", 3, "other"));

        var tip = store.GetTopicTreeSnapshot().Roots.Single(node => node.FullTopic == "root")
            .Children.Single();
        tip.HasDirectMessages.Should().BeFalse("both raw names were evicted");
        tip.HasObservedDirectTopic.Should().BeTrue();

        store.RemoveMatchingTopic("root//tip");
        store.GetTopicTreeSnapshot().Roots.Single(node => node.FullTopic == "root")
            .Children.Single().HasObservedDirectTopic.Should().BeTrue(
                "the canonical name is still recorded");

        store.RemoveMatchingTopic("root/tip");
        store.GetTopicTreeSnapshot().Roots.Should().NotContain(node => node.FullTopic == "root",
            "the last recorded raw name retired, so the emptied ancestry is pruned");
        store.TopicNodeCount.Should().Be(1);
    }

    [Test]
    public void RetainedHistoryChangesNoCounter()
    {
        var store = CreateStore(maxMessages: 1);
        store.Add("root//tip", Message("alias", 1, "root//tip"));
        store.Add("other", Message("other", 2, "other"));

        var before = store.GetTopicTreeSnapshot();
        store.TopicNodeCount.Should().Be(3, "root, root/tip and other");
        store.TotalStoredMessages.Should().Be(1);

        store.RemoveMatchingTopic("root/tip");

        store.TopicNodeCount.Should().Be(3, "history alone does not add or remove nodes");
        store.TotalStoredMessages.Should().Be(1, "an exclude that matches nothing removes no message");
    }

    [Test]
    public void RetainedHistoryStillConsumesTheNodeLimit()
    {
        var store = CreateStore(maxMessages: 1, maxTopicNodes: 2);
        store.Add("root//tip", Message("alias", 1, "root//tip"));
        store.Add("other", Message("other", 2, "other"));

        store.TopicNodeCount.Should().Be(2, "the rejected add did not create a node");
        store.TotalStoredMessages.Should().Be(1);

        store.Add("root//tip", Message("later", 2, "root//tip"));
        store.TopicNodeCount.Should().Be(2);
        store.TotalStoredMessages.Should().Be(1, "the alias message replaced the evicted one in place");
    }

    [Test]
    public void ClearDropsRetainedHistorySoRecreatedPathsStartFresh()
    {
        var store = CreateStore(maxMessages: 1);
        store.Add("root//tip", Message("alias", 1, "root//tip"));
        store.Add("other", Message("other", 2, "other"));

        store.Clear();

        store.Add("root/tip", Message("later", 3, "root/tip"));

        var root = store.GetTopicTreeSnapshot().Roots.Single();
        root.FullTopic.Should().Be("root");
        root.HasObservedDirectTopic.Should().BeFalse();
        root.Children.Single().FullTopic.Should().Be("root/tip");
        root.Children.Single().HasObservedDirectTopic.Should().BeTrue();
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
