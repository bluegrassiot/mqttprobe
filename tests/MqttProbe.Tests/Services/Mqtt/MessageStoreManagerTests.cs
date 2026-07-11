using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;
using MqttProbe.Models.Configuration;
using MqttProbe.Models.Mqtt;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Metrics;
using MqttProbe.Services.Mqtt;
using MqttProbe.Services.Sparkplug;

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
        _messageStoreManager = new MessageStoreManager(_mockClient, _mockLogger, mockSettings, Substitute.For<IUxMetricsService>(), mockDecoder);
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

    [Test]
    public async Task GetRecentMessagesAsync_ReturnsAtMostLimitMessages()
    {
        var messages = new ConcurrentQueue<MqttMessage>();
        for (int i = 0; i < 100; i++)
            messages.Enqueue(new MqttMessage { DateTimeReceived = DateTime.UtcNow.AddSeconds(-i) });

        var temp = new MessageStore { Topic = "temp", FullTopic = "sensors/temp", Messages = messages };
        var sensors = new MessageStore
        {
            Topic = "sensors",
            FullTopic = "sensors",
            SubTopics = new ConcurrentDictionary<string, MessageStore> { ["temp"] = temp }
        };
        _messageStoreManager.MessageStores.TryAdd("sensors", sensors);

        var result = await _messageStoreManager.GetRecentMessagesAsync("sensors/temp", 10);

        result.Should().HaveCount(10);
        result.Should().BeInDescendingOrder(m => m.DateTimeReceived);
    }

    [Test]
    public async Task GetRecentMessagesAsync_ReturnsAllWhenFewerThanLimit()
    {
        var messages = new ConcurrentQueue<MqttMessage>();
        for (int i = 0; i < 3; i++)
            messages.Enqueue(new MqttMessage { DateTimeReceived = DateTime.UtcNow.AddSeconds(-i) });

        var humidity = new MessageStore { Topic = "humidity", FullTopic = "sensors/humidity", Messages = messages };
        var sensors = new MessageStore
        {
            Topic = "sensors",
            FullTopic = "sensors",
            SubTopics = new ConcurrentDictionary<string, MessageStore> { ["humidity"] = humidity }
        };
        _messageStoreManager.MessageStores.TryAdd("sensors", sensors);

        var result = await _messageStoreManager.GetRecentMessagesAsync("sensors/humidity", 10);

        result.Should().HaveCount(3);
    }

    [Test]
    public void GetVersion_IncrementsAfterAdd()
    {
        long before = _messageStoreManager.GetVersion();
        _messageStoreManager.AddMessage("sensors/temp", new MqttMessage());
        long after = _messageStoreManager.GetVersion();

        after.Should().BeGreaterThan(before);
    }

    [Test]
    public void GetSelectedTopicVersion_IncrementsWhenSelectedTopicMessagesChange()
    {
        var store = new MessageStore { Topic = "temp", FullTopic = "sensors/temp" };
        var sensors = new MessageStore
        {
            Topic = "sensors",
            FullTopic = "sensors",
            SubTopics = new ConcurrentDictionary<string, MessageStore> { ["temp"] = store }
        };
        _messageStoreManager.MessageStores.TryAdd("sensors", sensors);
        _messageStoreManager.SelectedMessageStore = store;

        long before = _messageStoreManager.GetSelectedTopicVersion();
        _messageStoreManager.AddMessage("sensors/temp", new MqttMessage());
        long after = _messageStoreManager.GetSelectedTopicVersion();

        after.Should().BeGreaterThan(before);
    }

    [Test]
    public async Task GetRecentMessagesAsync_AggregatesDescendantMessagesForParentTopic()
    {
        var t1 = DateTime.UtcNow.AddSeconds(-10);
        var t2 = DateTime.UtcNow.AddSeconds(-5);
        var t3 = DateTime.UtcNow.AddSeconds(-1);

        var tempMessages = new ConcurrentQueue<MqttMessage>();
        tempMessages.Enqueue(new MqttMessage { DateTimeReceived = t1, Topic = "sensors/temp" });
        tempMessages.Enqueue(new MqttMessage { DateTimeReceived = t3, Topic = "sensors/temp" });
        var temp = new MessageStore { Topic = "temp", FullTopic = "sensors/temp", Messages = tempMessages };

        var humidityMessages = new ConcurrentQueue<MqttMessage>();
        humidityMessages.Enqueue(new MqttMessage { DateTimeReceived = t2, Topic = "sensors/humidity" });
        var humidity = new MessageStore { Topic = "humidity", FullTopic = "sensors/humidity", Messages = humidityMessages };

        var sensors = new MessageStore
        {
            Topic = "sensors",
            FullTopic = "sensors",
            SubTopics = new ConcurrentDictionary<string, MessageStore> { ["temp"] = temp, ["humidity"] = humidity }
        };
        _messageStoreManager.MessageStores.TryAdd("sensors", sensors);

        var result = await _messageStoreManager.GetRecentMessagesAsync("sensors", 10);

        result.Should().HaveCount(3);
        result.Should().BeInDescendingOrder(m => m.DateTimeReceived);
    }

    [Test]
    public async Task GetRecentMessagesAsync_ParentTopic_RespectsLimitAcrossDescendants()
    {
        var tempMessages = new ConcurrentQueue<MqttMessage>();
        for (int i = 0; i < 5; i++)
            tempMessages.Enqueue(new MqttMessage { DateTimeReceived = DateTime.UtcNow.AddSeconds(-i), Topic = "sensors/temp" });
        var temp = new MessageStore { Topic = "temp", FullTopic = "sensors/temp", Messages = tempMessages };

        var humidityMessages = new ConcurrentQueue<MqttMessage>();
        for (int i = 0; i < 5; i++)
            humidityMessages.Enqueue(new MqttMessage { DateTimeReceived = DateTime.UtcNow.AddSeconds(-i - 100), Topic = "sensors/humidity" });
        var humidity = new MessageStore { Topic = "humidity", FullTopic = "sensors/humidity", Messages = humidityMessages };

        var sensors = new MessageStore
        {
            Topic = "sensors",
            FullTopic = "sensors",
            SubTopics = new ConcurrentDictionary<string, MessageStore> { ["temp"] = temp, ["humidity"] = humidity }
        };
        _messageStoreManager.MessageStores.TryAdd("sensors", sensors);

        var result = await _messageStoreManager.GetRecentMessagesAsync("sensors", 3);

        result.Should().HaveCount(3);
        result.Should().BeInDescendingOrder(m => m.DateTimeReceived);
    }

    [Test]
    public void GetSelectedTopicVersion_IncrementsWhenDescendantMessagesChange()
    {
        var store = new MessageStore { Topic = "temp", FullTopic = "sensors/temp" };
        var sensors = new MessageStore
        {
            Topic = "sensors",
            FullTopic = "sensors",
            SubTopics = new ConcurrentDictionary<string, MessageStore> { ["temp"] = store }
        };
        _messageStoreManager.MessageStores.TryAdd("sensors", sensors);
        _messageStoreManager.SelectedMessageStore = sensors;

        long before = _messageStoreManager.GetSelectedTopicVersion();
        _messageStoreManager.AddMessage("sensors/temp", new MqttMessage());
        long after = _messageStoreManager.GetSelectedTopicVersion();

        after.Should().BeGreaterThan(before);
    }

    [Test]
    public void AddMessage_MaintainsTopicAndMessageCounts()
    {
        _messageStoreManager.AddMessage("sensors/temp/room1", new MqttMessage());
        _messageStoreManager.AddMessage("sensors/temp/room1", new MqttMessage());
        _messageStoreManager.AddMessage("sensors/temp", new MqttMessage());
        _messageStoreManager.AddMessage("sensors/humidity", new MqttMessage());
        _messageStoreManager.AddMessage("sensors/humidity", new MqttMessage());

        var sensors = _messageStoreManager.MessageStores["sensors"];
        var temp = sensors.SubTopics!["temp"];
        var room1 = temp.SubTopics!["room1"];
        var humidity = sensors.SubTopics!["humidity"];

        // TopicCount: total descendant topics in subtree
        sensors.TopicCount.Should().Be(3); // temp, room1, humidity
        temp.TopicCount.Should().Be(1);    // room1
        room1.TopicCount.Should().Be(0);
        humidity.TopicCount.Should().Be(0);

        // MessageCount: total messages in subtree (own + all descendants)
        sensors.MessageCount.Should().Be(5); // 3 from temp subtree + 2 from humidity
        temp.MessageCount.Should().Be(3);    // 1 own + 2 from room1
        room1.MessageCount.Should().Be(2);
        humidity.MessageCount.Should().Be(2);
    }

    [Test]
    public void AddMessage_LeadingSlash_NormalizesToCleanPath()
    {
        _messageStoreManager.AddMessage("/sensors/temp", new MqttMessage());

        _messageStoreManager.MessageStores.Should().ContainKey("sensors");
        var sensors = _messageStoreManager.MessageStores["sensors"];
        sensors.SubTopics.Should().ContainKey("temp");
        sensors.FullTopic.Should().Be("sensors");
        sensors.SubTopics["temp"].FullTopic.Should().Be("sensors/temp");
        _messageStoreManager.MessageStores.Should().NotContainKey("");
    }

    [Test]
    public void AddMessage_TrailingSlash_NormalizesToCleanPath()
    {
        _messageStoreManager.AddMessage("sensors/temp/", new MqttMessage());

        _messageStoreManager.MessageStores.Should().ContainKey("sensors");
        var temp = _messageStoreManager.MessageStores["sensors"].SubTopics!["temp"];
        temp.FullTopic.Should().Be("sensors/temp");
        temp.Messages.Should().NotBeNull();
        temp.Messages.Should().HaveCount(1);
    }

    [Test]
    public void AddMessage_DoubleSlash_NormalizesToCleanPath()
    {
        _messageStoreManager.AddMessage("a//b", new MqttMessage());

        _messageStoreManager.MessageStores.Should().ContainKey("a");
        var a = _messageStoreManager.MessageStores["a"];
        a.SubTopics.Should().ContainKey("b");
        a.SubTopics.Should().NotContainKey("");
        a.SubTopics!["b"].FullTopic.Should().Be("a/b");
    }

    [Test]
    public void AddMessage_AllSlashes_Rejected_StoreRemainsEmpty()
    {
        _messageStoreManager.AddMessage("///", new MqttMessage());

        _messageStoreManager.MessageStores.Should().BeEmpty();
    }

    [Test]
    public void AddMessage_CleanSparkplugTopic_Unchanged()
    {
        _messageStoreManager.AddMessage("spBv1.0/group/NBIRTH/node1", new MqttMessage());

        var root = _messageStoreManager.MessageStores["spBv1.0"];
        root.SubTopics!["group"].SubTopics!["NBIRTH"].SubTopics!["node1"]
            .Messages.Should().HaveCount(1);
    }

    [Test]
    public void AddMessage_LeadingSlash_PreservesOriginalTopicOnMessage()
    {
        var msg = new MqttMessage();
        _messageStoreManager.AddMessage("/sensors/temp", msg);

        var leaf = _messageStoreManager.MessageStores["sensors"].SubTopics!["temp"];
        leaf.Messages.Should().ContainSingle().Which.Should().BeSameAs(msg);
    }
}
