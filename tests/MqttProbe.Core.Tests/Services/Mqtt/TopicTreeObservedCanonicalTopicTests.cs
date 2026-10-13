using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Packets;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Metrics;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Tests.Utilities;

namespace MqttProbe.Core.Tests.Services.Mqtt;

// A node coalesces every spelling that normalizes onto its path, so the tree cannot tell by itself
// whether the published spelling is the supported Sparkplug endpoint shape or only an alias that
// normalizes onto it. HasObservedCanonicalTopic
// narrows HasObservedDirectTopic to the node's own normalized path; a node observed only through a
// malformed alias is a grouping prefix, not a topic an endpoint sits at.
[TestFixture]
public sealed class TopicTreeObservedCanonicalTopicTests
{
    [TestCase("root//tip")]
    [TestCase("root///tip")]
    [TestCase("/root/tip")]
    [TestCase("root/tip/")]
    public async Task AliasOnlySpelling_IsObservedButNotCanonical(string topic)
    {
        using var harness = await Harness.StartAsync();
        await harness.PublishAsync(topic, "alias");

        var tree = harness.Manager.GetTopicTreeSnapshot();
        var root = tree.Roots.Single();
        root.FullTopic.Should().Be("root");
        root.HasObservedDirectTopic.Should().BeFalse("the prefix itself was never published to");
        root.HasObservedCanonicalTopic.Should().BeFalse();

        var tip = root.Children.Single();
        tip.FullTopic.Should().Be("root/tip");
        tip.HasObservedDirectTopic.Should().BeTrue();
        tip.HasObservedCanonicalTopic.Should().BeFalse(
            "the published spelling is an alias, not the supported endpoint shape");
    }

    [Test]
    public async Task ExactCanonicalName_ObservesBothFlags()
    {
        using var harness = await Harness.StartAsync();
        await harness.PublishAsync("root/tip", "canonical");

        var tip = harness.Manager.GetTopicTreeSnapshot().Roots.Single().Children.Single();
        tip.FullTopic.Should().Be("root/tip");
        tip.HasDirectMessages.Should().BeTrue();
        tip.HasObservedDirectTopic.Should().BeTrue();
        tip.HasObservedCanonicalTopic.Should().BeTrue();
    }

    [Test]
    public async Task ExactCanonicalName_KeepsBothFlagsAfterItsMessageExpires()
    {
        using var harness = await Harness.StartAsync(maxStoredMessages: 2);
        await harness.PublishAsync("root/tip", "canonical");
        await harness.PublishAsync("root/other", "other");
        await harness.PublishAsync("root/third", "third");

        var tip = harness.Manager.GetTopicTreeSnapshot().Roots.Single().Children
            .Single(node => node.FullTopic == "root/tip");
        tip.HasDirectMessages.Should().BeFalse("retention expired the canonical message");
        tip.HasObservedDirectTopic.Should().BeTrue();
        tip.HasObservedCanonicalTopic.Should().BeTrue();
        harness.Manager.TotalStoredMessages.Should().Be(2);
    }

    [Test]
    public async Task MixedSpellingsCoalesceIntoOneNode()
    {
        using var harness = await Harness.StartAsync();
        await harness.PublishAsync("root//tip", "alias");
        await harness.PublishAsync("root/tip", "canonical");
        await harness.PublishAsync("root///tip", "other-alias");

        var tree = harness.Manager.GetTopicTreeSnapshot();
        var root = tree.Roots.Single();
        root.Children.Should().ContainSingle("every spelling normalizes onto one node");
        root.TopicCount.Should().Be(1);
        harness.Manager.TopicNodeCount.Should().Be(2);

        var tip = root.Children.Single();
        tip.MessageCount.Should().Be(3, "one node holds every spelling's message");
        tip.HasObservedDirectTopic.Should().BeTrue();
        tip.HasObservedCanonicalTopic.Should().BeTrue();
    }

    [Test]
    public async Task CanonicalExclude_RetiresOnlyTheCanonicalNameAndKeepsTheNode()
    {
        using var harness = await Harness.StartAsync(maxStoredMessages: 2);
        await harness.PublishAsync("root//tip", "alias");
        await harness.PublishAsync("root/tip", "canonical");
        await harness.PublishAsync("other", "other");
        await harness.PublishAsync("other", "other-again");

        var before = harness.Manager.GetTopicTreeSnapshot();
        var tipBefore = before.Roots.Single(node => node.FullTopic == "root").Children.Single();
        tipBefore.HasDirectMessages.Should().BeFalse("both spellings were expired by retention");
        tipBefore.HasObservedCanonicalTopic.Should().BeTrue();

        (await harness.Excludes.Add("root/tip")).IsValid.Should().BeTrue();
        var after = harness.Manager.GetTopicTreeSnapshot();

        after.Should().NotBeSameAs(before, "the purge invalidated the cached snapshot");
        after.Version.Should().BeGreaterThan(before.Version);

        var tip = after.Roots.Single(node => node.FullTopic == "root").Children.Single();
        tip.FullTopic.Should().Be("root/tip");
        tip.HasObservedDirectTopic.Should().BeTrue("the alias raw name is still recorded");
        tip.HasObservedCanonicalTopic.Should().BeFalse("the canonical raw name retired");
        harness.Manager.TotalStoredMessages.Should().Be(2,
            "retention holds two, and neither is the excluded canonical message");
    }

    [Test]
    public async Task AliasExclude_PreservesTheCanonicalRecord()
    {
        using var harness = await Harness.StartAsync();
        await harness.PublishAsync("root//tip", "alias");
        await harness.PublishAsync("root/tip", "canonical");

        (await harness.Excludes.Add("root//tip")).IsValid.Should().BeTrue();

        var tip = harness.Manager.GetTopicTreeSnapshot().Roots.Single(node => node.FullTopic == "root")
            .Children.Single();
        tip.HasDirectMessages.Should().BeTrue("the canonical message is not excluded by the alias");
        tip.HasObservedDirectTopic.Should().BeTrue();
        tip.HasObservedCanonicalTopic.Should().BeTrue();
        harness.Manager.TotalStoredMessages.Should().Be(1);
    }

    [Test]
    public async Task CanonicalRepublish_RestoresTheCanonicalFlagAfterACanonicalExclude()
    {
        using var harness = await Harness.StartAsync();
        await harness.PublishAsync("root//tip", "alias");
        await harness.PublishAsync("root/tip", "canonical");

        (await harness.Excludes.Add("root/tip")).IsValid.Should().BeTrue();
        harness.Manager.GetTopicTreeSnapshot().Roots.Single(node => node.FullTopic == "root")
            .Children.Single().HasObservedCanonicalTopic.Should().BeFalse();

        (await harness.Excludes.Remove(["root/tip"])).IsValid.Should().BeTrue();
        await harness.PublishAsync("root/tip", "republished");

        var tip = harness.Manager.GetTopicTreeSnapshot().Roots.Single(node => node.FullTopic == "root")
            .Children.Single();
        tip.HasDirectMessages.Should().BeTrue();
        tip.HasObservedDirectTopic.Should().BeTrue();
        tip.HasObservedCanonicalTopic.Should().BeTrue();
    }

    [Test]
    public async Task TopicLessMessage_RecordsTheNodePathAndIsCanonical()
    {
        using var harness = await Harness.StartAsync();
        await harness.PublishAsync("root/tip", "canonical");

        // MQTTnet refuses to build a wire message without a topic, so the topic-less fallback is
        // driven through the manager's own add path rather than through a hand-built fixture.
        harness.Manager.AddMessage("root/child", new MqttMessage
        {
            Payload = "topic-less",
            Topic = null
        });

        var child = harness.Manager.GetTopicTreeSnapshot().Roots.Single().Children
            .Single(node => node.FullTopic == "root/child");
        child.HasObservedDirectTopic.Should().BeTrue();
        child.HasObservedCanonicalTopic.Should().BeTrue(
            "a message with no topic of its own is recorded under the path it reached");

        (await harness.Excludes.Add("root/child")).IsValid.Should().BeTrue();

        harness.Manager.GetTopicTreeSnapshot().Roots.Single(node => node.FullTopic == "root")
            .Children.Should().NotContain(node => node.FullTopic == "root/child");
    }

    [Test]
    public async Task StructuralPrefixObservesNeitherFlag()
    {
        using var harness = await Harness.StartAsync();
        await harness.PublishAsync("root/tip", "canonical");

        var root = harness.Manager.GetTopicTreeSnapshot().Roots.Single();
        root.HasDirectMessages.Should().BeFalse();
        root.HasObservedDirectTopic.Should().BeFalse();
        root.HasObservedCanonicalTopic.Should().BeFalse();
    }

    [Test]
    public void LegacySixArgumentSnapshot_DefaultsBothObservedFlagsToDirectMessages()
    {
        var observed = new TopicNodeSnapshot("a", "a", 0, 1, true, []);
        var structural = new TopicNodeSnapshot("b", "b", 1, 0, false, []);

        observed.HasObservedDirectTopic.Should().BeTrue();
        observed.HasObservedCanonicalTopic.Should().BeTrue();
        structural.HasObservedDirectTopic.Should().BeFalse();
        structural.HasObservedCanonicalTopic.Should().BeFalse();

        var (topic, fullTopic, topicCount, messageCount, hasDirectMessages, children) = observed;
        topic.Should().Be("a");
        fullTopic.Should().Be("a");
        topicCount.Should().Be(0);
        messageCount.Should().Be(1);
        hasDirectMessages.Should().BeTrue();
        children.Should().BeEmpty();
    }

    private static MqttApplicationMessageReceivedEventArgs MakeArgs(string? topic, string payload)
    {
        var applicationMessage = topic is null
            ? new MqttApplicationMessageBuilder().WithPayload(payload).Build()
            : new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload).Build();
        return new MqttApplicationMessageReceivedEventArgs(
            "test-client",
            applicationMessage,
            new MqttPublishPacket { Topic = topic },
            null);
    }

    private sealed class Harness : IDisposable
    {
        private readonly Func<MqttApplicationMessageReceivedEventArgs, Task> _handler;

        private Harness(
            MessageStoreManager manager,
            TopicExcludeService excludes,
            Func<MqttApplicationMessageReceivedEventArgs, Task> handler)
        {
            Manager = manager;
            Excludes = excludes;
            _handler = handler;
        }

        public MessageStoreManager Manager { get; }

        public TopicExcludeService Excludes { get; }

        public static async Task<Harness> StartAsync(int maxStoredMessages = 100)
        {
            var client = Substitute.For<IMqttManagedClient>();
            Func<MqttApplicationMessageReceivedEventArgs, Task>? handler = null;
            client.When(x => x.ApplicationMessageReceivedAsync +=
                    Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>())
                .Do(x => handler = x.Arg<Func<MqttApplicationMessageReceivedEventArgs, Task>>());

            var session = new SessionState { SelectedConnection = new Connection() };
            var excludes = new TopicExcludeService(
                Substitute.For<ILogger<TopicExcludeService>>(),
                Substitute.For<IConnectionSettings>(),
                session);

            var performance = Substitute.For<IPerformanceSettings>();
            performance.Performance.Returns(new PerformanceSettings
            {
                MaxStoredMessages = maxStoredMessages,
                MaxTopicNodes = 100
            });
            var sparkplug = Substitute.For<ISparkplugSettings>();
            sparkplug.Sparkplug.Returns(new SparkplugSettings());

            var manager = new MessageStoreManager(client, Substitute.For<ILogger<MessageStoreManager>>(),
                performance, Substitute.For<IUxMetricsService>(), TestPipelineHelper.BuildBuiltInPipeline(),
                sparkplug, topicExcludeService: excludes);
            await manager.Start();

            return new Harness(manager, excludes, handler!);
        }

        public Task PublishAsync(string? topic, string payload) => _handler(MakeArgs(topic, payload));

        public void Dispose()
        {
            Manager.Dispose();
            Excludes.Dispose();
        }
    }
}
