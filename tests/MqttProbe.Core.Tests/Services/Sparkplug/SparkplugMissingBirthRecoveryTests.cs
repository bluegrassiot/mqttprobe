using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MQTTnet;
using MQTTnet.Packets;
using MQTTnet.Protocol;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Sparkplug;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Metrics;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Core.Services.Sparkplug;
using MqttProbe.Tests.Utilities;
using Org.Eclipse.Tahu.Protobuf;

namespace MqttProbe.Core.Tests.Services.Sparkplug;

[TestFixture]
public class SparkplugMissingBirthRecoveryTests
{
    private const string GroupId = "recovery-group";
    private const string NodeId = "node1";

    private RebirthProbeClient _client = null!;
    private SparkplugTopologyService _topology = null!;
    private MessageStoreManager _manager = null!;
    private FakeTimeProvider _clock = null!;

    [SetUp]
    public void SetUp()
    {
        _client = new RebirthProbeClient();
        _clock = new FakeTimeProvider();
        var sparkplug = Substitute.For<ISparkplugSettings>();
        sparkplug.Sparkplug.Returns(new SparkplugSettings
        {
            EnrichAliasNames = true,
            AutoRequestRebirth = true,
            RebirthCooldownSeconds = 30,
            ReorderTimeoutMilliseconds = 0
        });
        _topology = new SparkplugTopologyService(sparkplugSettings: sparkplug);
        var commands = new SparkplugCommandService(
            _client, Substitute.For<ILogger<SparkplugCommandService>>(), sparkplug, _topology, _clock);
        var performance = Substitute.For<IPerformanceSettings>();
        performance.Performance.Returns(new AppConfiguration().Performance);
        _manager = new MessageStoreManager(
            _client, Substitute.For<ILogger<MessageStoreManager>>(), performance,
            Substitute.For<IUxMetricsService>(), TestPipelineHelper.BuildBuiltInPipeline(),
            sparkplug, _topology, commands);
    }

    [TearDown]
    public void TearDown()
    {
        _manager.Dispose();
        _client.Dispose();
    }

    [Test]
    public async Task MatchingNodeDeath_WithoutFollowingData_RequestsNothing()
    {
        await _manager.Start();
        await DeliverBirthAsync(1);
        await DeliverNodeDeathAsync();

        Node().Status.Should().Be(SpbNodeStatus.Offline);
        // The sequence monitor ticks every 100 ms, so this spans several ticks: a wrongly
        // eligible offline node would have queued a rebirth by now.
        await Task.Delay(400);

        _client.QueuedCount.Should().Be(0, "an idle offline node has no traffic to justify a rebirth");
        _client.ConnectedPublishCount.Should().Be(0);
    }

    [Test]
    public async Task NodeDataAfterMatchingNodeDeath_RequestsExactlyOneQueuedRebirth()
    {
        await _manager.Start();
        await DeliverBirthAsync(1);
        await DeliverNodeDeathAsync();

        await DeliverNodeDataAsync(5);
        await DeliverNodeDataAsync(6);

        _client.QueuedCount.Should().Be(1, "the cooldown has to suppress the second data message");
        _client.ConnectedPublishCount.Should().Be(0, "a missing birth is recovered through the queued path");
        _client.QueuedTopics.Should().Equal($"spBv1.0/{GroupId}/NCMD/{NodeId}");
    }

    [Test]
    public async Task DeviceDataAfterMatchingNodeDeath_RequestsQueuedRebirth()
    {
        await _manager.Start();
        await DeliverBirthAsync(1);
        await DeliverNodeDeathAsync();

        await DeliverDeviceDataAsync("d1", 5);

        _client.QueuedCount.Should().Be(1);
        _client.QueuedTopics.Should().Equal($"spBv1.0/{GroupId}/NCMD/{NodeId}");
    }

    [Test]
    public async Task DataAfterNodeDeath_DoesNotMarkTheNodeOnline()
    {
        await _manager.Start();
        await DeliverBirthAsync(1);
        await DeliverNodeDeathAsync();

        await DeliverNodeDataAsync(5);

        Node().Status.Should().Be(SpbNodeStatus.Offline,
            "only a birth can bring a node back online");
        _client.QueuedCount.Should().Be(1);
    }

    [Test]
    public async Task FirstDataFromUnknownNode_WithUnusableSequence_RequestsRebirth()
    {
        await _manager.Start();

        await DeliverNodeDataAsync(300);

        var node = Node();
        node.Status.Should().Be(SpbNodeStatus.Unknown);
        node.Sequence.State.Should().Be(SpbSequenceState.Invalid);
        _client.QueuedCount.Should().Be(1,
            "sequence validity decides gap recovery, not missing-birth recovery");
        _client.QueuedTopics.Should().Equal($"spBv1.0/{GroupId}/NCMD/{NodeId}");
    }

    [Test]
    public async Task OfflineNodeWithGap_QueuesSecondRebirthOnlyAfterCooldownAndFreshData()
    {
        await _manager.Start();
        await DeliverBirthAsync(1);
        await DeliverNodeDeathAsync();
        await DeliverNodeDataAsync(5);
        await DeliverNodeDataAsync(7);

        _client.QueuedCount.Should().Be(1, "the unresolved gap appears before the cooldown expires");

        _clock.Advance(TimeSpan.FromSeconds(31));
        await DeliverNodeDataAsync(8);

        _client.QueuedCount.Should().Be(2,
            "an offline node with an incomplete observation must still recover after the cooldown");

        _clock.Advance(TimeSpan.FromSeconds(120));
        // The sequence monitor ticks every 100 ms, so idle time alone must not retry.
        await Task.Delay(400);

        _client.QueuedCount.Should().Be(2, "recovery is data-triggered, so an idle node must not keep asking");
    }

    [Test]
    public async Task OnlineNodeWithGap_KeepsConnectedRecoveryAfterNodeData()
    {
        await _manager.Start();
        await DeliverBirthAsync(1);

        await DeliverNodeDataAsync(3);
        await WaitForAsync(() => _client.ConnectedPublishCount == 1);

        _client.ConnectedPublishEpoch.Should().Be(_client.Epoch);
        _client.QueuedCount.Should().Be(0);
    }

    private SpbNode Node() => _topology.Groups[GroupId].Nodes[NodeId];

    private Task DeliverBirthAsync(ulong sequence) =>
        _client.DeliverAsync($"spBv1.0/{GroupId}/NBIRTH/{NodeId}", PayloadWithSequence(sequence, birthDeathSequence: 1u));

    private Task DeliverNodeDeathAsync() =>
        _client.DeliverAsync($"spBv1.0/{GroupId}/NDEATH/{NodeId}", PayloadWithSequence(0, birthDeathSequence: 1u));

    private Task DeliverNodeDataAsync(ulong sequence) =>
        _client.DeliverAsync($"spBv1.0/{GroupId}/NDATA/{NodeId}", PayloadWithSequence(sequence, birthDeathSequence: null));

    private Task DeliverDeviceDataAsync(string deviceId, ulong sequence) =>
        _client.DeliverAsync(
            $"spBv1.0/{GroupId}/DDATA/{NodeId}/{deviceId}",
            PayloadWithSequence(sequence, birthDeathSequence: null));

    private static Payload PayloadWithSequence(ulong sequence, uint? birthDeathSequence)
    {
        var payload = new Payload { Timestamp = 1, Seq = sequence };
        if (birthDeathSequence is { } bdSeq)
            payload.Metrics.Add(new Payload.Types.Metric { Name = "bdSeq", Datatype = 5, IntValue = bdSeq });
        payload.Metrics.Add(new Payload.Types.Metric
        {
            Name = "temperature",
            Datatype = 10,
            DoubleValue = 21.5
        });
        return payload;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && !condition())
            await Task.Delay(25);

        condition().Should().BeTrue("the expected state was not reached within the bounded wait");
    }

    private sealed class RebirthProbeClient : IMqttManagedClient
    {
        private Func<MqttApplicationMessageReceivedEventArgs, Task>? _messageHandler;

        public long Epoch { get; } = 1;
        public long? InboundConnectionEpoch => Epoch;
        public long? ConnectionEpoch => Epoch;
        public MqttSubscriptionSnapshot? LiveSubscriptionSnapshot { get; } = new(1, 1, ["spBv1.0/#"]);
        public MqttSubscriptionSnapshot? InboundSubscriptionSnapshot { get; } = new(1, 1, ["spBv1.0/#"]);
        public int QueuedCount { get; private set; }
        public List<string> QueuedTopics { get; } = [];
        public int ConnectedPublishCount { get; private set; }
        public long? ConnectedPublishEpoch { get; private set; }

        public bool IsConnected => true;
        public bool IsStarted => true;

        public bool IsCurrentAttempt(MqttConnectAttemptId attemptId) => false;

        public event Func<MqttClientConnectedEventArgs, Task>? ConnectedAsync
        {
            add { }
            remove { }
        }

        public event Func<MqttClientDisconnectedEventArgs, Task>? DisconnectedAsync
        {
            add { }
            remove { }
        }

        public event Func<MqttConnectingFailedEventArgs, Task>? ConnectingFailedAsync
        {
            add { }
            remove { }
        }

        public event Func<EventArgs, Task>? ConnectionStateChangedAsync
        {
            add { }
            remove { }
        }

        public event Func<MqttApplicationMessageReceivedEventArgs, Task>? ApplicationMessageReceivedAsync
        {
            add => _messageHandler += value;
            remove => _messageHandler -= value;
        }

        public event Func<MqttManagedProcessFailedEventArgs, Task>? SynchronizingSubscriptionsFailedAsync
        {
            add { }
            remove { }
        }

        public event Func<MqttConnectOutcome, Task>? ConnectAttemptCompletedAsync
        {
            add { }
            remove { }
        }

        public Task DeliverAsync(string topic, Payload payload)
        {
            var applicationMessage = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload.ToByteArray())
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();
            var packet = new MqttPublishPacket { Topic = topic };
            var args = new MqttApplicationMessageReceivedEventArgs("rebirth-probe", applicationMessage, packet, null);
            return _messageHandler?.Invoke(args) ?? Task.CompletedTask;
        }

        public Task EnqueueAsync(MqttApplicationMessage applicationMessage, CancellationToken cancellationToken = default)
        {
            QueuedCount++;
            QueuedTopics.Add(applicationMessage.Topic);
            return Task.CompletedTask;
        }

        public Task<bool> TryPublishConnectedAsync(
            MqttApplicationMessage applicationMessage,
            long? expectedConnectionEpoch = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> TryPublishConnectedAsync(
            MqttApplicationMessage applicationMessage,
            long? expectedConnectionEpoch,
            long? expectedSubscriptionRevision,
            CancellationToken cancellationToken)
        {
            ConnectedPublishCount++;
            ConnectedPublishEpoch = expectedConnectionEpoch;
            return Task.FromResult(true);
        }

        public Task SubscribeAsync(IEnumerable<MqttTopicFilter> topicFilters, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task UnsubscribeAsync(IEnumerable<string> topics, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task StartAsync(MqttManagedClientOptions options, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public MqttConnectAttempt StartAttempt(MqttManagedClientOptions options, CancellationToken cancellationToken = default) =>
            new(MqttConnectAttemptId.None,
                Task.FromResult(new MqttConnectOutcome(MqttConnectAttemptId.None, MqttConnectStatus.Connected)));

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAttemptAsync(MqttConnectAttemptId attemptId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public void Dispose() { }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
