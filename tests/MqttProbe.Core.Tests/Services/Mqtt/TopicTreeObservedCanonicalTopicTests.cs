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

[TestFixture]
public sealed class TopicTreeObservedCanonicalTopicTests
{
    [TestCase("root//tip", "root")]
    [TestCase("root///tip", "root")]
    [TestCase("/root/tip", "")]
    [TestCase("root/tip/", "root")]
    public async Task RawSpelling_KeepsItsOwnStructuralPath(string topic, string expectedRoot)
    {
        using var harness = await Harness.StartAsync();
        await harness.PublishAsync(topic, "payload");

        var tree = harness.Manager.GetTopicTreeSnapshot();
        tree.Roots.Select(node => node.FullTopic).Should().Equal(expectedRoot);

        var tip = Node(tree, topic);
        tip.HasDirectMessages.Should().BeTrue();
        tip.HasObservedDirectTopic.Should().BeTrue();
        tip.HasObservedCanonicalTopic.Should().BeTrue("the recorded raw name is the node's own path");

        AllNodes(tree).Where(node => node.HasDirectMessages).Select(node => node.FullTopic)
            .Should().ContainSingle().Which.Should()
            .Be(topic, "no cleaned spelling is merged onto this node");
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
    public async Task MixedSpellingsStayOnDistinctNodes()
    {
        using var harness = await Harness.StartAsync();
        await harness.PublishAsync("root//tip", "alias");
        await harness.PublishAsync("root/tip", "canonical");
        await harness.PublishAsync("root///tip", "other-alias");

        var tree = harness.Manager.GetTopicTreeSnapshot();
        tree.Roots.Single().FullTopic.Should().Be("root");

        AllNodes(tree).Where(node => node.HasDirectMessages).Select(node => node.FullTopic)
            .Should().BeEquivalentTo(["root//tip", "root/tip", "root///tip"]);
        harness.Manager.TopicNodeCount.Should().Be(6, "each raw level is its own node");

        Node(tree, "root/tip").MessageCount.Should().Be(1, "no other spelling merges onto it");
        Node(tree, "root//tip").HasObservedDirectTopic.Should().BeTrue();
        Node(tree, "root///tip").HasObservedDirectTopic.Should().BeTrue();
    }

    [Test]
    public async Task RawExclude_RetiresOnlyThatPathAndKeepsTheSibling()
    {
        using var harness = await Harness.StartAsync(maxStoredMessages: 2);
        await harness.PublishAsync("root//tip", "slashed");
        await harness.PublishAsync("root/tip", "plain");
        await harness.PublishAsync("other", "other");
        await harness.PublishAsync("other", "other-again");

        var before = harness.Manager.GetTopicTreeSnapshot();
        Node(before, "root/tip").HasDirectMessages.Should().BeFalse("both spellings were expired by retention");
        Node(before, "root/tip").HasObservedCanonicalTopic.Should().BeTrue();

        (await harness.Excludes.Add("root/tip")).IsValid.Should().BeTrue();
        var after = harness.Manager.GetTopicTreeSnapshot();

        after.Should().NotBeSameAs(before, "the purge invalidated the cached snapshot");
        after.Version.Should().BeGreaterThan(before.Version);

        AllNodes(after).Should().NotContain(node => node.FullTopic == "root/tip");
        Node(after, "root//tip").HasObservedDirectTopic.Should().BeTrue(
            "the sibling raw path is a different node and keeps its history");
        harness.Manager.TotalStoredMessages.Should().Be(2,
            "retention holds two, and neither is the excluded message");
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
    public async Task ExcludedRawPath_RecreatesOnRepublish()
    {
        using var harness = await Harness.StartAsync();
        await harness.PublishAsync("root//tip", "slashed");
        await harness.PublishAsync("root/tip", "plain");

        (await harness.Excludes.Add("root/tip")).IsValid.Should().BeTrue();
        AllNodes(harness.Manager.GetTopicTreeSnapshot())
            .Should().NotContain(node => node.FullTopic == "root/tip");

        (await harness.Excludes.Remove(["root/tip"])).IsValid.Should().BeTrue();
        await harness.PublishAsync("root/tip", "republished");

        var tip = Node(harness.Manager.GetTopicTreeSnapshot(), "root/tip");
        tip.HasDirectMessages.Should().BeTrue();
        tip.HasObservedDirectTopic.Should().BeTrue();
        tip.HasObservedCanonicalTopic.Should().BeTrue();
    }

    [Test]
    public async Task TopicLessMessage_RecordsTheNodePathAndIsCanonical()
    {
        using var harness = await Harness.StartAsync();
        await harness.PublishAsync("root/tip", "canonical");

        // MQTTnet will not build a wire message without a topic, so the null-topic fallback goes
        // through the manager's own add path instead of a hand-built fixture.
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

    private static IEnumerable<TopicNodeSnapshot> AllNodes(TopicTreeSnapshot tree) =>
        tree.Roots.SelectMany(Flatten);

    private static IEnumerable<TopicNodeSnapshot> Flatten(TopicNodeSnapshot node) =>
        node.Children.SelectMany(Flatten).Prepend(node);

    private static TopicNodeSnapshot Node(TopicTreeSnapshot tree, string fullTopic) =>
        AllNodes(tree).Single(node => node.FullTopic == fullTopic);

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
