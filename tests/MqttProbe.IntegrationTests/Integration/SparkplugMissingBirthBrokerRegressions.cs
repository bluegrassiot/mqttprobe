using Google.Protobuf;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Packets;
using MQTTnet.Protocol;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Sparkplug;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Metrics;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Core.Services.Plugins.BuiltIn;
using MqttProbe.Core.Services.Plugins.Pipeline;
using MqttProbe.Core.Services.Plugins.Registry;
using MqttProbe.Core.Services.Sparkplug;
using Org.Eclipse.Tahu.Protobuf;

namespace MqttProbe.IntegrationTests.Integration;

public sealed partial class SparkplugSequenceRecoveryTests
{
    [Test]
    public async Task EarlierConnectedSubscriberDeliveringBirth_KeepsBaselineAndGapRecovery()
    {
        var groupId = $"connected-dispatch-{Guid.NewGuid():N}";
        const string nodeId = "node";
        // A device filter carries a literal verb, so DBIRTH, DDATA and DDEATH each need their own
        // filter or the birth lands with incomplete coverage and gap recovery never qualifies.
        var nodeFilters = new[] { $"spBv1.0/{groupId}/NBIRTH/{nodeId}", $"spBv1.0/{groupId}/NDATA/{nodeId}" }
            .Concat(new[] { "DBIRTH", "DDATA", "DDEATH" }
                .Select(verb => $"spBv1.0/{groupId}/{verb}/{nodeId}/+"))
            .Select(Filter)
            .ToArray();
        var subscriptionTopics = nodeFilters.Select(filter => filter.Topic).ToArray();
        var client = new MqttManagedClient();
        var publisher = new MqttClientFactory().CreateMqttClient();
        var observer = new MqttClientFactory().CreateMqttClient();
        var rebirthReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rebirthCount = 0;
        var sparkplug = Substitute.For<ISparkplugSettings>();
        sparkplug.Sparkplug.Returns(new SparkplugSettings
        {
            EnrichAliasNames = true,
            AutoRequestRebirth = true,
            RebirthCooldownSeconds = 5,
            ReorderTimeoutMilliseconds = 250
        });
        var topology = new SparkplugTopologyService(sparkplugSettings: sparkplug);
        var commands = new SparkplugCommandService(
            client, Substitute.For<ILogger<SparkplugCommandService>>(), sparkplug, topology);
        var performance = Substitute.For<IPerformanceSettings>();
        performance.Performance.Returns(new PerformanceSettings
        {
            MaxStoredMessages = 100,
            MaxMessagesPerSecond = 50_000
        });
        using var store = new MessageStoreManager(
            client,
            Substitute.For<ILogger<MessageStoreManager>>(),
            performance,
            Substitute.For<IUxMetricsService>(),
            BuildRebuildPipeline(),
            sparkplug,
            topology,
            commands);
        try
        {
            await observer.ConnectAsync(BuildTlsConnectOptions("connected-observer", _broker).ClientOptions);
            await observer.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(Filter($"spBv1.0/{groupId}/NCMD/{nodeId}"))
                .Build());
            observer.ApplicationMessageReceivedAsync += args =>
            {
                Interlocked.Increment(ref rebirthCount);
                rebirthReceived.TrySetResult(args.ApplicationMessage.Topic);
                return Task.CompletedTask;
            };
            await publisher.ConnectAsync(BuildTlsConnectOptions("connected-publisher", _broker).ClientOptions);

            // Registered before the store starts, so this subscriber observes the connection
            // before the store's own handler and a reset there would wipe the birth below.
            client.ConnectedAsync += async _ =>
            {
                await WaitForAsync(
                    () => client.LiveSubscriptionSnapshot is { } snapshot
                        && subscriptionTopics.All(topic => snapshot.ConfirmedFilters.Contains(topic)),
                    TimeSpan.FromSeconds(8),
                    "the restored subscriptions were not confirmed before connected dispatch finished");
                await publisher.PublishAsync(SparkplugMessage($"spBv1.0/{groupId}/NBIRTH/{nodeId}", 1, birthDeathSequence: 1u));
                await WaitForAsync(
                    () => NodeBaseline(topology, groupId, nodeId) is { } node && node.Sequence.ExpectedSequence == 2,
                    TimeSpan.FromSeconds(8),
                    "the birth published during connected dispatch was never processed");
            };

            await store.Start();
            await client.SubscribeAsync(nodeFilters);
            await client.StartAsync(BuildTlsConnectOptions("connected-dispatch", _broker));

            var node = NodeBaseline(topology, groupId, nodeId);
            node.Should().NotBeNull("connected dispatch has to deliver the birth from the earlier subscriber");
            node!.Status.Should().Be(SpbNodeStatus.Online);
            node.Sequence.State.Should().Be(SpbSequenceState.InOrder);
            node.Sequence.ExpectedSequence.Should().Be((byte)2,
                "connected dispatch must not reset the baseline the earlier subscriber established");

            await publisher.PublishAsync(SparkplugMessage($"spBv1.0/{groupId}/NDATA/{nodeId}", 4, birthDeathSequence: null));
            await WaitForAsync(
                () => NodeBaseline(topology, groupId, nodeId)?.Sequence.State == SpbSequenceState.GapDetected,
                TimeSpan.FromSeconds(5),
                "the surviving baseline never turned the skipped sequences into an unresolved gap");

            (await rebirthReceived.Task.WaitAsync(TimeSpan.FromSeconds(8)))
                .Should().Be($"spBv1.0/{groupId}/NCMD/{nodeId}",
                "the surviving baseline and coverage still have to qualify for gap recovery");
            Volatile.Read(ref rebirthCount).Should().Be(1);
        }
        finally
        {
            await store.Stop();
            await client.StopAsync();
            await publisher.DisconnectAsync();
            await observer.DisconnectAsync();
            publisher.Dispose();
            observer.Dispose();
            await client.DisposeAsync();
        }
    }

    [Test]
    public async Task NodeDeathWithoutReplacementBirth_FollowingDataPublishesRebirthToTheBroker()
    {
        await using var context = await SequenceContext.CreateAsync(_broker!, "missing-birth");
        await context.Client.SubscribeAsync([Filter($"spBv1.0/{context.GroupId}/NDEATH/{context.NodeId}")]);
        await context.PublishBirthAsync(1);
        await context.WaitForStateAsync(SpbSequenceState.InOrder);

        using var publisher = new MqttClientFactory().CreateMqttClient();
        await publisher.ConnectAsync(BuildTlsConnectOptions("missing-birth-publisher", _broker).ClientOptions);
        await publisher.PublishAsync(
            SparkplugMessage($"spBv1.0/{context.GroupId}/NDEATH/{context.NodeId}", 0, birthDeathSequence: 1u));
        await WaitForNodeAsync(context, node => node.Status == SpbNodeStatus.Offline);

        await context.PublishDataAsync(5);

        (await context.NodeRebirthReceived.Task.WaitAsync(TimeSpan.FromSeconds(8)))
            .Should().Be($"spBv1.0/{context.GroupId}/NCMD/{context.NodeId}");
        context.NodeRebirthCount.Should().Be(1);
        context.Topology.Groups[context.GroupId].Nodes[context.NodeId].Status.Should().Be(SpbNodeStatus.Offline,
            "data must not bring a dead node back online");
    }

    private static MqttTopicFilter Filter(string topic) =>
        new MqttTopicFilterBuilder().WithTopic(topic)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build();

    private static SpbNode? NodeBaseline(ISparkplugTopologyService topology, string groupId, string nodeId) =>
        topology.Groups.TryGetValue(groupId, out var group)
        && group.Nodes.TryGetValue(nodeId, out var node)
            ? node
            : null;

    private static async Task WaitForNodeAsync(SequenceContext context, Func<SpbNode, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline
               && (!context.Topology.Groups.TryGetValue(context.GroupId, out var group)
                   || !group.Nodes.TryGetValue(context.NodeId, out var node)
                   || !condition(node)))
            await Task.Delay(25);

        condition(context.Topology.Groups[context.GroupId].Nodes[context.NodeId])
            .Should().BeTrue("the node did not reach the expected state within the bounded wait");
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout, string because)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline && !condition())
            await Task.Delay(25);

        condition().Should().BeTrue(because);
    }

    private static PayloadPipeline BuildRebuildPipeline()
    {
        var builder = new PluginRegistryBuilder();
        BuiltInPluginRegistration.RegisterBuiltIns(builder);
        return new PayloadPipeline(builder.Build(), Substitute.For<ILogger<PayloadPipeline>>());
    }

    private static MqttApplicationMessage SparkplugMessage(string topic, ulong sequence, uint? birthDeathSequence)
    {
        var payload = new Payload { Timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Seq = sequence };
        if (birthDeathSequence is { } bdSeq)
            payload.Metrics.Add(new Payload.Types.Metric { Name = "bdSeq", Datatype = 5, IntValue = bdSeq });
        payload.Metrics.Add(new Payload.Types.Metric { Name = "temperature", Datatype = 10, DoubleValue = 21.5 });
        return new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload.ToByteArray())
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();
    }
}
