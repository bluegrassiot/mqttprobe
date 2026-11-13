using Google.Protobuf;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Protocol;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Sparkplug;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Metrics;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Core.Services.Sparkplug;
using Org.Eclipse.Tahu.Protobuf;

namespace MqttProbe.IntegrationTests.Integration;

public sealed partial class SparkplugSequenceRecoveryTests
{
    [Test]
    public async Task SubscribedNcmdNodeRebirth_UpdatesKnownNodeWithoutStatusOrSequenceChange()
    {
        var groupId = $"ncmd-observed-{Guid.NewGuid():N}";
        const string nodeId = "node";
        var birthTopic = $"spBv1.0/{groupId}/NBIRTH/{nodeId}";
        var commandTopic = $"spBv1.0/{groupId}/NCMD/{nodeId}";

        var (client, store, topology, publisher) = await StartObservationStackAsync(
            [birthTopic, commandTopic]);
        var commandReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.MessageReceived += message =>
        {
            if (message.Topic == commandTopic)
                commandReceived.TrySetResult();
            return Task.CompletedTask;
        };

        try
        {
            await publisher.PublishAsync(SparkplugMessage(birthTopic, 1, birthDeathSequence: 1u));
            await WaitForAsync(
                () => NodeBaseline(topology, groupId, nodeId) is { Status: SpbNodeStatus.Online },
                TimeSpan.FromSeconds(8),
                "the broker birth was not applied before the rebirth command");

            var node = topology.Groups[groupId].Nodes[nodeId];
            var sequence = node.Sequence;
            node.LastRebirthRequestAt.Should().BeNull();

            await publisher.PublishAsync(RebirthCommand(commandTopic, true));
            await commandReceived.Task.WaitAsync(TimeSpan.FromSeconds(8));

            node.LastRebirthRequestAt.Should().NotBeNull();
            node.LastRebirthRequestAt.Should().BeOnOrAfter(DateTime.UtcNow.AddMinutes(-1));
            node.Status.Should().Be(SpbNodeStatus.Online);
            node.LastDataAt.Should().BeNull();
            node.Sequence.Should().Be(sequence);
        }
        finally
        {
            await StopObservationStackAsync(client, store, publisher);
        }
    }

    [Test]
    public async Task SubscribedNcmdNodeRebirth_ForUnknownNode_AddsNoTopology()
    {
        var groupId = $"ncmd-orphan-{Guid.NewGuid():N}";
        const string nodeId = "node";
        var commandTopic = $"spBv1.0/{groupId}/NCMD/{nodeId}";

        var (client, store, topology, publisher) = await StartObservationStackAsync([commandTopic]);
        var commandReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.MessageReceived += message =>
        {
            if (message.Topic == commandTopic)
                commandReceived.TrySetResult();
            return Task.CompletedTask;
        };

        try
        {
            var changed = 0;
            topology.TopologyChanged += () => Interlocked.Increment(ref changed);

            await publisher.PublishAsync(RebirthCommand(commandTopic, true));
            await commandReceived.Task.WaitAsync(TimeSpan.FromSeconds(8));

            topology.Groups.Should().NotContainKey(groupId);
            changed.Should().Be(0);
        }
        finally
        {
            await StopObservationStackAsync(client, store, publisher);
        }
    }

    private async Task<(MqttManagedClient Client, MessageStoreManager Store,
        SparkplugTopologyService Topology, IMqttClient Publisher)> StartObservationStackAsync(
        IReadOnlyList<string> topics)
    {
        var sparkplug = Substitute.For<ISparkplugSettings>();
        sparkplug.Sparkplug.Returns(new SparkplugSettings { EnrichAliasNames = true });
        var performance = Substitute.For<IPerformanceSettings>();
        performance.Performance.Returns(new AppConfiguration().Performance);
        var topology = new SparkplugTopologyService(sparkplugSettings: sparkplug);
        var client = new MqttManagedClient();
        var store = new MessageStoreManager(
            client,
            Substitute.For<ILogger<MessageStoreManager>>(),
            performance,
            Substitute.For<IUxMetricsService>(),
            BuildRebuildPipeline(),
            sparkplug,
            topology);
        var publisher = new MqttClientFactory().CreateMqttClient();

        await store.Start();
        await client.SubscribeAsync(topics.Select(Filter));
        await client.StartAsync(BuildTlsConnectOptions("ncmd-observer"));
        await publisher.ConnectAsync(BuildTlsConnectOptions("ncmd-publisher").ClientOptions);
        return (client, store, topology, publisher);
    }

    private static async Task StopObservationStackAsync(
        MqttManagedClient client, MessageStoreManager store, IMqttClient publisher)
    {
        await store.Stop();
        await client.StopAsync();
        if (publisher.IsConnected)
            await publisher.DisconnectAsync();
        await client.DisposeAsync();
        store.Dispose();
        publisher.Dispose();
    }

    private static MqttApplicationMessage RebirthCommand(string topic, bool value)
    {
        var payload = new Payload { Timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
        payload.Metrics.Add(new Payload.Types.Metric
        {
            Name = SparkplugControlMetrics.NodeRebirth,
            Datatype = 11,
            BooleanValue = value
        });
        return new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload.ToByteArray())
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();
    }
}
