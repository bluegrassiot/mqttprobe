using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Packets;
using MQTTnet.Protocol;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Metrics;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Tests.Utilities;

namespace MqttProbe.Core.Tests.Services.Mqtt;

[TestFixture]
public class FirstConnectRetainedMessageRaceTests
{
    private FakeRetainedClient _fakeClient = null!;
    private ILogger<SubscriptionManager> _subLogger = null!;
    private ILogger<MessageStoreManager> _storeLogger = null!;
    private SubscriptionManager _subscriptionManager = null!;
    private MessageStoreManager _storeManager = null!;
    private IPerformanceSettings _perfSettings = null!;

    [SetUp]
    public void SetUp()
    {
        _fakeClient = new FakeRetainedClient();
        _subLogger = Substitute.For<ILogger<SubscriptionManager>>();
        _storeLogger = Substitute.For<ILogger<MessageStoreManager>>();

        var config = new AppConfiguration
        {
            Performance = new PerformanceSettings { MaxStoredMessages = 100, MaxMessagesPerSecond = 50_000 }
        };
        _perfSettings = Substitute.For<IPerformanceSettings>();
        _perfSettings.Performance.Returns(config.Performance);

        var sparkplug = Substitute.For<ISparkplugSettings>();
        sparkplug.Sparkplug.Returns(new SparkplugSettings { EnrichAliasNames = true });

        var connection = new Connection
        {
            Name = "Test",
            Host = "localhost",
            SubscribedTopics =
            [
                new SubscribedTopic { Topic = "race/retained", QualityOfServiceLevel = MqttQualityOfServiceLevel.AtLeastOnce }
            ]
        };

        var uiSettings = Substitute.For<IUiSettings>();
        var uiConfig = new AppConfiguration { Ui = new UiPreferences { AutoResubscribe = true } };
        uiSettings.Ui.Returns(uiConfig.Ui);

        var sessionState = Substitute.For<ISessionState>();
        sessionState.SelectedConnection.Returns(connection);

        var notifier = new FakeUserNotifier();

        _subscriptionManager = new SubscriptionManager(
            _fakeClient, _subLogger, notifier,
            Substitute.For<IConnectionSettings>(), uiSettings, sessionState);

        _storeManager = new MessageStoreManager(
            _fakeClient, _storeLogger, _perfSettings,
            Substitute.For<IUxMetricsService>(), TestPipelineHelper.BuildBuiltInPipeline(), sparkplug);
    }

    [TearDown]
    public void TearDown()
    {
        _storeManager.Dispose();
        _subscriptionManager.Dispose();
        _fakeClient.Dispose();
    }

    [Test]
    public async Task Start_BeforeConnect_RetainedMessage_IsStored()
    {
        await _storeManager.Start();

        await _fakeClient.FireConnectedAsync();

        _fakeClient.SubscribeAsyncCallCount.Should().Be(1);

        _storeManager.MessageStores.Should().ContainKey("race");
        var messages = _storeManager.MessageStores["race"].SubTopics!["retained"].Messages;
        messages.Should().NotBeNull();
        messages.Should().ContainSingle()
            .Which.Payload.Should().Be("hello-retained");
    }

    [Test]
    public async Task Start_AfterConnect_RetainedMessage_IsLost_ComponentContract()
    {
        _fakeClient.ApplicationMessageReceivedHandlers.Should().BeEmpty(
            "MessageStoreManager.Start() has not been called yet");

        await _fakeClient.FireConnectedAsync();

        _fakeClient.SubscribeAsyncCallCount.Should().Be(1);

        _storeManager.MessageStores.Should().BeEmpty(
            "retained message arrived with no ApplicationMessageReceived handler; it was dropped");
        _storeManager.TotalStoredMessages.Should().Be(0);

        await _storeManager.Start();
        await _fakeClient.DeliverRetainedMessageAsync();

        _storeManager.MessageStores.Should().ContainKey("race");
        var messages = _storeManager.MessageStores["race"].SubTopics!["retained"].Messages;
        messages.Should().NotBeNull();
        messages.Should().ContainSingle()
            .Which.Payload.Should().Be("hello-retained");
    }

#pragma warning disable CS0067 // unused events required by IMqttManagedClient interface
    private sealed class FakeRetainedClient : IMqttManagedClient
    {
        private Func<MqttApplicationMessageReceivedEventArgs, Task>? _messageHandler;
        private Func<MqttClientConnectedEventArgs, Task>? _connectedHandler;

        public bool IsConnected => true;
        public bool IsStarted => true;

        public event Func<MqttClientConnectedEventArgs, Task>? ConnectedAsync
        {
            add => _connectedHandler += value;
            remove => _connectedHandler -= value;
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

        public int SubscribeAsyncCallCount { get; private set; }

        public List<Func<MqttApplicationMessageReceivedEventArgs, Task>> ApplicationMessageReceivedHandlers =>
            _messageHandler?.GetInvocationList()
                .Select(d => (Func<MqttApplicationMessageReceivedEventArgs, Task>)d)
                .ToList() ?? [];

        public Task SubscribeAsync(IEnumerable<MqttTopicFilter> topicFilters, CancellationToken cancellationToken = default)
        {
            SubscribeAsyncCallCount++;

            return DeliverRetainedMessageAsync();
        }

        public Task DeliverRetainedMessageAsync()
        {
            if (_messageHandler is not { } handler)
                return Task.CompletedTask;

            var appMsg = new MqttApplicationMessageBuilder()
                .WithTopic("race/retained")
                .WithPayload("hello-retained")
                .WithRetainFlag()
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();
            var packet = new MqttPublishPacket { Topic = "race/retained" };
            var args = new MqttApplicationMessageReceivedEventArgs("test-client", appMsg, packet, null);

            return handler.Invoke(args);
        }

        public Task FireConnectedAsync() =>
            _connectedHandler?.Invoke(new MqttClientConnectedEventArgs(new MqttClientConnectResult()))
            ?? Task.CompletedTask;

        public Task UnsubscribeAsync(IEnumerable<string> topics, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task EnqueueAsync(MqttApplicationMessage applicationMessage, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task StartAsync(MqttManagedClientOptions options, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
#pragma warning restore CS0067
}
