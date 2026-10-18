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
using MqttProbe.Core.Services.Sparkplug;
using MqttProbe.Tests.Utilities;
using Org.Eclipse.Tahu.Protobuf;

namespace MqttProbe.Core.Tests.Services.Mqtt;

[TestFixture]
public class MessageStoreManagerConnectedDispatchTests
{
    private const string GroupId = "dispatch-group";
    private const string NodeId = "node1";

    private DispatchClient _client = null!;
    private SparkplugTopologyService _topology = null!;
    private MessageStoreManager _manager = null!;

    [SetUp]
    public void SetUp()
    {
        _client = new DispatchClient();
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
            _client, Substitute.For<ILogger<SparkplugCommandService>>(), sparkplug, _topology);
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
    public async Task ConnectedDispatch_KeepsBirthDeliveredByEarlierSubscriber_AndStillRecoversGaps()
    {
        var delivered = false;
        _client.OnConnected = async (MqttClientConnectedEventArgs _) =>
        {
            delivered = true;
            await DeliverBirthAsync(1);
        };
        await _manager.Start();

        await _client.RaiseConnectedAsync();

        delivered.Should().BeTrue("the earlier subscriber has to deliver the birth during connected dispatch");
        AssertCompleteBaseline(1, expectedSequence: 2);
        await AssertGapRecoveryAsync(3);
    }

    [Test]
    public async Task ConnectedDispatch_AfterSequentialData_KeepsBaselineAndGapRecovery()
    {
        await _manager.Start();
        await DeliverBirthAsync(1);
        await DeliverDataAsync(2);

        await _client.RaiseConnectedAsync();

        AssertCompleteBaseline(1, expectedSequence: 3);
        await AssertGapRecoveryAsync(4);
    }

    [Test]
    public async Task Reconnect_FreshBirthDeliveredDuringDispatch_KeepsNewBaseline()
    {
        await _manager.Start();
        await DeliverBirthAsync(1);
        AssertCompleteBaseline(1, expectedSequence: 2);

        await _client.RaiseDisconnectedAsync();
        _client.SetSubscriptionContext(epoch: 2, revision: 7);
        _client.OnConnected = async (MqttClientConnectedEventArgs _) => await DeliverBirthAsync(20);

        await _client.RaiseConnectedAsync();

        AssertCompleteBaseline(20, expectedSequence: 21, expectedEpoch: 2, expectedRevision: 7);
        await AssertGapRecoveryAsync(22);
    }

    [Test]
    public async Task ConnectedDispatch_DoesNotCompleteBirthSeenBeforeSubscribeAcknowledgement()
    {
        _client.LiveSubscriptionSnapshot = null;
        _client.InboundSubscriptionSnapshot = null;
        await _manager.Start();
        await DeliverBirthAsync(1);

        var acknowledged = new MqttSubscriptionSnapshot(1, 1, ["spBv1.0/#"]);
        _client.LiveSubscriptionSnapshot = acknowledged;
        _client.InboundSubscriptionSnapshot = acknowledged;

        await _client.RaiseConnectedAsync();
        await DeliverDataAsync(2);

        var node = Node();
        node.SequenceObservationComplete.Should().BeFalse(
            "a birth delivered before the subscribe acknowledgement cannot prove coverage");
        node.SequenceSubscriptionRevision.Should().BeNull();
        _client.ConnectedPublishCount.Should().Be(0);
    }

    private void AssertCompleteBaseline(
        ulong birthSequence,
        byte expectedSequence,
        long? expectedEpoch = 1,
        long? expectedRevision = 1)
    {
        var node = Node();
        node.Status.Should().Be(SpbNodeStatus.Online);
        node.LastBirthSequenceNumber.Should().Be(birthSequence);
        node.Sequence.State.Should().Be(SpbSequenceState.InOrder);
        node.Sequence.ExpectedSequence.Should().Be(expectedSequence);
        node.SequenceObservationComplete.Should().BeTrue();
        node.SequenceConnectionEpoch.Should().Be(expectedEpoch);
        node.SequenceSubscriptionRevision.Should().Be(expectedRevision);
    }

    private async Task AssertGapRecoveryAsync(ulong dataSequence)
    {
        await DeliverDataAsync(dataSequence);

        await WaitForAsync(() => _client.ConnectedPublishCount == 1);
        _client.ConnectedPublishEpoch.Should().Be(_client.Epoch);
        _client.ConnectedPublishRevision.Should().Be(_client.InboundSubscriptionSnapshot?.Revision);
    }

    private SpbNode Node() => _topology.Groups[GroupId].Nodes[NodeId];

    private Task DeliverBirthAsync(ulong sequence) =>
        _client.DeliverAsync($"spBv1.0/{GroupId}/NBIRTH/{NodeId}", PayloadWithSequence(sequence, birthDeathSequence: 1u));

    private Task DeliverDataAsync(ulong sequence) =>
        _client.DeliverAsync($"spBv1.0/{GroupId}/NDATA/{NodeId}", PayloadWithSequence(sequence, birthDeathSequence: null));

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

    private sealed class DispatchClient : IMqttManagedClient
    {
        private Func<MqttApplicationMessageReceivedEventArgs, Task>? _messageHandler;
        private Func<MqttClientConnectedEventArgs, Task>? _connectedHandlers;
        private Func<MqttClientDisconnectedEventArgs, Task>? _disconnectedHandlers;

        public long Epoch { get; private set; } = 1;
        public long? InboundConnectionEpoch => Epoch;
        public long? ConnectionEpoch => Epoch;
        public MqttSubscriptionSnapshot? LiveSubscriptionSnapshot { get; set; }
        public MqttSubscriptionSnapshot? InboundSubscriptionSnapshot { get; set; }
        public Func<MqttClientConnectedEventArgs, Task>? OnConnected { get; set; }
        public int ConnectedPublishCount { get; private set; }
        public long? ConnectedPublishEpoch { get; private set; }
        public long? ConnectedPublishRevision { get; private set; }

        public bool IsConnected => true;
        public bool IsStarted => true;

        public bool IsCurrentAttempt(MqttConnectAttemptId attemptId) => false;

        public event Func<MqttClientConnectedEventArgs, Task>? ConnectedAsync
        {
            add => _connectedHandlers += value;
            remove => _connectedHandlers -= value;
        }

        public event Func<MqttClientDisconnectedEventArgs, Task>? DisconnectedAsync
        {
            add => _disconnectedHandlers += value;
            remove => _disconnectedHandlers -= value;
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

        public DispatchClient()
        {
            SetSubscriptionContext(epoch: 1, revision: 1);
        }

        public void SetSubscriptionContext(long epoch, long revision)
        {
            Epoch = epoch;
            var snapshot = new MqttSubscriptionSnapshot(epoch, revision, ["spBv1.0/#"]);
            LiveSubscriptionSnapshot = snapshot;
            InboundSubscriptionSnapshot = snapshot;
        }

        public async Task RaiseConnectedAsync()
        {
            if (OnConnected is { } onConnected)
                await onConnected(new MqttClientConnectedEventArgs(new MqttClientConnectResult()));
            if (_connectedHandlers is { } handlers)
                await handlers(new MqttClientConnectedEventArgs(new MqttClientConnectResult()));
        }

        public async Task RaiseDisconnectedAsync()
        {
            if (_disconnectedHandlers is { } handlers)
                await handlers(new MqttClientDisconnectedEventArgs(
                    true, null!, MqttClientDisconnectReason.NormalDisconnection, null!, null!, null!));
        }

        public Task DeliverAsync(string topic, Payload payload)
        {
            var applicationMessage = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload.ToByteArray())
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();
            var packet = new MqttPublishPacket { Topic = topic };
            var args = new MqttApplicationMessageReceivedEventArgs("dispatch-client", applicationMessage, packet, null);
            return _messageHandler?.Invoke(args) ?? Task.CompletedTask;
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
            ConnectedPublishRevision = expectedSubscriptionRevision;
            return Task.FromResult(true);
        }

        public Task EnqueueAsync(MqttApplicationMessage applicationMessage, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

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
