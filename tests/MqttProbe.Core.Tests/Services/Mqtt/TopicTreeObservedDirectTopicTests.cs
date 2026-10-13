using Microsoft.Extensions.Logging;
using MQTTnet;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Metrics;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Tests.Utilities;

namespace MqttProbe.Core.Tests.Services.Mqtt;

// HasObservedDirectTopic separates a real topic that a direct message once reached from a
// structural prefix that only exists because a deeper topic did. Retention keeps the history;
// exclusion and reset retire it.
[TestFixture]
public sealed class TopicTreeObservedDirectTopicTests
{
    [Test]
    public void StructuralPrefix_HasNoObservedHistory()
    {
        var store = CreateStore();
        store.Add("root/child", Message("child", 1, "root/child"));

        var root = store.GetTopicTreeSnapshot().Roots.Single();

        root.FullTopic.Should().Be("root");
        root.HasDirectMessages.Should().BeFalse();
        root.HasObservedDirectTopic.Should().BeFalse("nothing was published at the prefix itself");
        root.Children.Single().FullTopic.Should().Be("root/child");
        root.Children.Single().HasObservedDirectTopic.Should().BeTrue();
    }

    [Test]
    public void LegacySixArgumentSnapshot_DefaultsObservedHistoryToDirectMessages()
    {
        var observed = new TopicNodeSnapshot("a", "a", 0, 1, true, []);
        var structural = new TopicNodeSnapshot("b", "b", 1, 0, false, []);

        observed.HasObservedDirectTopic.Should().BeTrue();
        structural.HasObservedDirectTopic.Should().BeFalse();

        var (topic, fullTopic, topicCount, messageCount, hasDirectMessages, children) = observed;
        topic.Should().Be("a");
        fullTopic.Should().Be("a");
        topicCount.Should().Be(0);
        messageCount.Should().Be(1);
        hasDirectMessages.Should().BeTrue();
        children.Should().BeEmpty();
    }

    [Test]
    public void RetentionEviction_KeepsObservedHistoryWithoutTouchingCounts()
    {
        var store = CreateStore(maxMessages: 1);
        store.Add("one", Message("first", 1, "one"));
        store.Add("two", Message("second", 2, "two"));

        var tree = store.GetTopicTreeSnapshot();
        var emptied = tree.Roots.Single(node => node.FullTopic == "one");

        store.TotalStoredMessages.Should().Be(1);
        emptied.MessageCount.Should().Be(0);
        emptied.HasDirectMessages.Should().BeFalse();
        emptied.HasObservedDirectTopic.Should().BeTrue("the eviction drained the queue, not the history");
        tree.Roots.Single(node => node.FullTopic == "two").HasObservedDirectTopic.Should().BeTrue();
    }

    [Test]
    public void RetentionEvictingEndpointAndDescendant_LeavesBothHistoriesTrue()
    {
        var store = CreateStore(maxMessages: 1);
        store.Add("root", Message("endpoint", 1, "root"));
        store.Add("root/child", Message("descendant", 2, "root/child"));
        store.Add("other", Message("newer", 3, "other"));

        var root = store.GetTopicTreeSnapshot().Roots.Single(node => node.FullTopic == "root");
        var child = root.Children.Single();

        root.MessageCount.Should().Be(0);
        child.MessageCount.Should().Be(0);
        root.HasDirectMessages.Should().BeFalse();
        child.HasDirectMessages.Should().BeFalse();
        root.HasObservedDirectTopic.Should().BeTrue("the endpoint was observed before eviction");
        child.HasObservedDirectTopic.Should().BeTrue("the descendant was observed before eviction");
    }

    [Test]
    public void PartialPurgeOfARawAlias_KeepsHistoryWhileADirectMessageSurvives()
    {
        var store = CreateStore();
        store.Add("root//tip", Message("alias", 1, "root//tip"));
        store.Add("root/tip", Message("canonical", 2, "root/tip"));

        store.GetTopicTreeSnapshot().Roots.Single().Children.Single().MessageCount.Should().Be(2);

        store.RemoveMatchingTopic("root//tip");

        var after = store.GetTopicTreeSnapshot().Roots.Single().Children.Single();
        after.FullTopic.Should().Be("root/tip");
        after.HasDirectMessages.Should().BeTrue("the canonical message survives the alias purge");
        after.HasObservedDirectTopic.Should().BeTrue();
        after.MessageCount.Should().Be(1);
        after.TopicCount.Should().Be(0);
        store.TotalStoredMessages.Should().Be(1);
    }

    [Test]
    public void ExactExclusionAfterEviction_RetiresHistoryButKeepsUnmatchedDescendants()
    {
        var store = CreateStore(maxMessages: 1);
        store.Add("root", Message("direct", 1, "root"));
        store.Add("root/kept", Message("descendant", 2, "root/kept"));

        var before = store.GetTopicTreeSnapshot();
        var evictedRoot = before.Roots.Single();
        evictedRoot.HasDirectMessages.Should().BeFalse();
        evictedRoot.HasObservedDirectTopic.Should().BeTrue("the direct message was evicted, not excluded");
        evictedRoot.Children.Single().MessageCount.Should().Be(1);

        store.RemoveMatchingTopic("root");
        var after = store.GetTopicTreeSnapshot();

        // Only the history flag changed, so the purge must still advance the version and drop
        // the cached snapshot.
        after.Should().NotBeSameAs(before);
        after.Version.Should().BeGreaterThan(before.Version);

        var root = after.Roots.Single();
        root.FullTopic.Should().Be("root");
        root.HasObservedDirectTopic.Should().BeFalse();
        root.HasDirectMessages.Should().BeFalse();
        root.TopicCount.Should().Be(evictedRoot.TopicCount, "counts ignore the history flag");
        root.MessageCount.Should().Be(evictedRoot.MessageCount);
        root.Children.Single().FullTopic.Should().Be("root/kept");
        root.Children.Single().HasObservedDirectTopic.Should().BeTrue();
    }

    [Test]
    public void DescendantOnlyExclusion_RetainsAncestorHistory()
    {
        var store = CreateStore();
        store.Add("root", Message("direct", 1, "root"));
        store.Add("root/child", Message("child", 2, "root/child"));

        store.RemoveMatchingTopic("root/child");

        var root = store.GetTopicTreeSnapshot().Roots.Single();
        root.HasObservedDirectTopic.Should().BeTrue();
        root.HasDirectMessages.Should().BeTrue();
        root.Children.Should().BeEmpty();
    }

    [Test]
    public void PurgedPath_StartsFreshWhenRecreated()
    {
        var store = CreateStore();
        store.Add("root", Message("direct", 1, "root"));
        store.RemoveMatchingTopic("root");
        store.GetTopicTreeSnapshot().Roots.Should().BeEmpty();

        store.Add("root/child", Message("later", 2, "root/child"));

        var root = store.GetTopicTreeSnapshot().Roots.Single();
        root.FullTopic.Should().Be("root");
        root.HasObservedDirectTopic.Should().BeFalse("the recreated prefix was never observed directly");
        root.Children.Single().HasObservedDirectTopic.Should().BeTrue();
    }

    [Test]
    public void Clear_ResetsObservedHistoryForRecreatedPaths()
    {
        var store = CreateStore();
        store.Add("root", Message("direct", 1, "root"));
        store.Clear();

        store.Add("root/child", Message("later", 2, "root/child"));

        store.GetTopicTreeSnapshot().Roots.Single().HasObservedDirectTopic.Should().BeFalse();
    }

    [Test]
    public void PublishingAtAnExistingPrefix_UpdatesTheCachedSnapshot()
    {
        var store = CreateStore();
        store.Add("root/child", Message("child", 1, "root/child"));
        var before = store.GetTopicTreeSnapshot();
        before.Roots.Single().HasObservedDirectTopic.Should().BeFalse();

        store.Add("root", Message("direct", 2, "root"));

        var after = store.GetTopicTreeSnapshot();
        after.Should().NotBeSameAs(before);
        after.Version.Should().BeGreaterThan(before.Version);
        after.Roots.Single().HasObservedDirectTopic.Should().BeTrue();
        after.Roots.Single().HasDirectMessages.Should().BeTrue();
    }

    [Test]
    public void RejectedNodeLimitAdd_LeavesNoObservedHistory()
    {
        var store = CreateStore(maxTopicNodes: 2);
        store.Add("root/child/leaf", Message("dropped", 1, "root/child/leaf"));

        var root = store.GetTopicTreeSnapshot().Roots.Single();
        root.Children.Single().FullTopic.Should().Be("root/child");
        root.HasObservedDirectTopic.Should().BeFalse();
        root.Children.Single().HasObservedDirectTopic.Should().BeFalse();
        store.TotalStoredMessages.Should().Be(0);
    }

    [Test]
    public async Task ExcludedTopic_KeepsNoObservedHistoryAndCannotReacquireIt()
    {
        var client = Substitute.For<IMqttManagedClient>();
        Func<MqttApplicationMessageReceivedEventArgs, Task>? handler = null;
        client.When(c => c.ApplicationMessageReceivedAsync +=
                Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>())
            .Do(call => handler = call.Arg<Func<MqttApplicationMessageReceivedEventArgs, Task>>());

        var session = new SessionState { SelectedConnection = new Connection() };
        using var excludes = new TopicExcludeService(
            Substitute.For<ILogger<TopicExcludeService>>(),
            Substitute.For<IConnectionSettings>(), session);
        var performance = Substitute.For<IPerformanceSettings>();
        performance.Performance.Returns(new AppConfiguration().Performance);
        var sparkplug = Substitute.For<ISparkplugSettings>();
        sparkplug.Sparkplug.Returns(new SparkplugSettings());

        using var manager = new MessageStoreManager(client, Substitute.For<ILogger<MessageStoreManager>>(),
            performance, Substitute.For<IUxMetricsService>(), TestPipelineHelper.BuildBuiltInPipeline(), sparkplug,
            topicExcludeService: excludes);
        await manager.Start();

        await handler!(MakeArgs("sensors/temp", "before-exclusion"));
        await handler!(MakeArgs("sensors/humidity", "sibling"));

        var observed = manager.GetTopicTreeSnapshot().Roots.Single().Children
            .Single(node => node.FullTopic == "sensors/temp");
        observed.HasObservedDirectTopic.Should().BeTrue();

        (await excludes.Add("sensors/temp")).IsValid.Should().BeTrue();

        var purged = manager.GetTopicTreeSnapshot().Roots.Single().Children;
        purged.Should().ContainSingle(node => node.FullTopic == "sensors/humidity");
        purged.Should().NotContain(node => node.FullTopic == "sensors/temp");

        await handler!(MakeArgs("sensors/temp", "blocked-during-exclusion"));

        var blocked = manager.GetTopicTreeSnapshot().Roots.Single();
        blocked.FullTopic.Should().Be("sensors");
        blocked.HasObservedDirectTopic.Should().BeFalse();
        blocked.Children.Should().NotContain(node => node.FullTopic == "sensors/temp");
        blocked.Children.Single().FullTopic.Should().Be("sensors/humidity");
        blocked.Children.Single().HasObservedDirectTopic.Should().BeTrue("the sibling keeps its own history");
    }

    private static MqttApplicationMessageReceivedEventArgs MakeArgs(string topic, string payload)
    {
        var appMsg = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .Build();
        var publishPacket = new MQTTnet.Packets.MqttPublishPacket { Topic = topic };
        return new MqttApplicationMessageReceivedEventArgs("test-client", appMsg, publishPacket, null);
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
