using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;
using MqttProbe.Models.Chart;
using MqttProbe.Models.Configuration;
using MqttProbe.Models.Mqtt;
using MqttProbe.Models.Sparkplug;
using MqttProbe.Services.Chart;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Mqtt;
using MqttProbe.Services.Platform;
using MqttProbe.Services.Security;
using MqttProbe.Services.Sparkplug;
using MqttProbe.Services.Telemetry;

namespace MqttProbe.Shared.Tests.Services.Mqtt;

[TestFixture]
public class MessageStoreManagerTests
{
    private IManagedMqttClient _mockClient;
    private ILogger<MessageStoreManager> _mockLogger;
    private MessageStoreManager _messageStoreManager;

    [SetUp]
    public void Setup()
    {
        _mockClient = Substitute.For<IManagedMqttClient>();
        _mockLogger = Substitute.For<ILogger<MessageStoreManager>>();
        var mockSettings = Substitute.For<ISettingsStore>();
        mockSettings.Config.Returns(new AppConfiguration());
        var mockDecoder = Substitute.For<IPayloadDecoder>();
        mockDecoder.Decode(Arg.Any<MqttApplicationMessageReceivedEventArgs>())
            .Returns(x =>
            {
                var e = (MqttApplicationMessageReceivedEventArgs)x[0];
                var seg = e.ApplicationMessage.PayloadSegment;
                var payload = seg.Count > 0 ? System.Text.Encoding.UTF8.GetString(seg.Array!, seg.Offset, seg.Count) : string.Empty;
                return new DecodedPayload(payload, DetectedPayloadFormat.PlainText);
            });
        _messageStoreManager = new MessageStoreManager(_mockClient, _mockLogger, mockSettings, Substitute.For<IUxTelemetryService>(), mockDecoder);
    }

    [TearDown]
    public void TearDown()
    {
        _messageStoreManager.Dispose();
        _mockClient.Dispose();
    }

    [Test]
    public void Constructor_Should_InitializeProperties()
    {
        _messageStoreManager.Should().NotBeNull();
        _messageStoreManager.MessageStores.Should().BeOfType<ConcurrentDictionary<string, MessageStore>>();
        _messageStoreManager.SelectedMessageStore.Should().BeNull();
        _messageStoreManager.IsListening.Should().BeFalse();
    }

    [Test]
    public async Task Start_Should_SetIsListeningToTrue_And_SubscribeToMessages()
    {

        await _messageStoreManager.Start();


        _messageStoreManager.IsListening.Should().BeTrue();
        _mockClient.Received(1).ApplicationMessageReceivedAsync += Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>();
    }

    [Test]
    public async Task Start_ConcurrentCalls_SubscribeOnlyOnce()
    {
        _mockClient
            .When(x => x.ApplicationMessageReceivedAsync += Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>())
            .Do(_ => Thread.Sleep(20));

        var starts = Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => _messageStoreManager.Start()));

        await Task.WhenAll(starts);

        _mockClient.Received(1).ApplicationMessageReceivedAsync += Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>();
    }

    [Test]
    public async Task Stop_Should_SetIsListeningToFalse_And_UnsubscribeFromMessages()
    {

        await _messageStoreManager.Start();


        await _messageStoreManager.Stop();


        _messageStoreManager.IsListening.Should().BeFalse();
        _mockClient.Received(1).ApplicationMessageReceivedAsync -= Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>();
    }

    [Test]
    public async Task Stop_ConcurrentCalls_UnsubscribeOnlyOnce()
    {
        await _messageStoreManager.Start();
        _mockClient.ClearReceivedCalls();
        _mockClient
            .When(x => x.ApplicationMessageReceivedAsync -= Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>())
            .Do(_ => Thread.Sleep(20));

        var stops = Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => _messageStoreManager.Stop()));

        await Task.WhenAll(stops);

        _messageStoreManager.IsListening.Should().BeFalse();
        _mockClient.Received(1).ApplicationMessageReceivedAsync -= Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>();
    }

    [Test]
    public async Task GetMessagesForSelectedTopic_Should_ReturnEmpty_WhenNoMessagesExist()
    {

        _messageStoreManager.SelectedMessageStore = null;


        var result = await _messageStoreManager.GetMessagesForSelectedTopic();


        result.Should().BeEmpty();
    }

    [Test]
    public async Task GetMessagesForSelectedTopic_Should_ReturnMessages_WhenMessagesExist()
    {

        var messages = new ConcurrentQueue<MqttMessage>();
        messages.Enqueue(new MqttMessage());
        var messageStore = new MessageStore { Messages = messages };
        _messageStoreManager.SelectedMessageStore = messageStore;


        var result = await _messageStoreManager.GetMessagesForSelectedTopic();


        result.Should().HaveCount(1);
    }

    [Test]
    public void MessageStores_Should_BeInitializedProperly()
    {

        _messageStoreManager.MessageStores.Should().NotBeNull();
        _messageStoreManager.MessageStores.Should().BeEmpty();
    }

    [Test]
    public async Task ClearAllMessages_Should_ClearStoreAndSelection()
    {
        var queue = new ConcurrentQueue<MqttMessage>();
        queue.Enqueue(new MqttMessage());
        var store = new MessageStore { Topic = "root", FullTopic = "root", Messages = queue };
        _messageStoreManager.MessageStores.TryAdd("root", store).Should().BeTrue();
        _messageStoreManager.SelectedMessageStore = store;

        await _messageStoreManager.ClearAllMessages();

        _messageStoreManager.MessageStores.Should().BeEmpty();
        _messageStoreManager.SelectedMessageStore.Should().BeNull();
    }

    [Test]
    public async Task ClearAllMessages_Should_NotThrow_WhenStoreIsEmpty()
    {
        await FluentActions
            .Invoking(async () => await _messageStoreManager.ClearAllMessages())
            .Should().NotThrowAsync();
    }

    [Test]
    public async Task Dispose_UnsubscribesApplicationMessageReceivedAsync_SoHandlerNoLongerFires()
    {
        await _messageStoreManager.Start();
        _mockClient.ClearReceivedCalls();

        _messageStoreManager.Dispose();

        _mockClient.Received(1).ApplicationMessageReceivedAsync -= Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>();
    }

    [Test]
    public async Task GetMessagesForSelectedTopic_AfterClearAllMessages_Should_ReturnEmpty()
    {
        var childMessages = new ConcurrentQueue<MqttMessage>();
        childMessages.Enqueue(new MqttMessage { Topic = "root/child", Payload = "x" });

        var child = new MessageStore { Topic = "child", FullTopic = "root/child", Messages = childMessages };
        var root = new MessageStore
        {
            Topic = "root",
            FullTopic = "root",
            SubTopics = new ConcurrentDictionary<string, MessageStore>()
        };
        root.SubTopics.TryAdd("child", child).Should().BeTrue();
        _messageStoreManager.MessageStores.TryAdd("root", root).Should().BeTrue();
        _messageStoreManager.SelectedMessageStore = root;

        await _messageStoreManager.ClearAllMessages();
        var result = await _messageStoreManager.GetMessagesForSelectedTopic();

        result.Should().BeEmpty();
    }
}
