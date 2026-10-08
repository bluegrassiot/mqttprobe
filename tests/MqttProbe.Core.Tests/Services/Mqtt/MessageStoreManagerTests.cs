using Microsoft.Extensions.Logging;
using MQTTnet;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Metrics;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.TestInfrastructure;
using MqttProbe.Tests.Utilities;

namespace MqttProbe.Core.Tests.Services.Mqtt;

[TestFixture]
public class MessageStoreManagerTests
{
    private IMqttManagedClient _mockClient = null!;
    private ILogger<MessageStoreManager> _mockLogger = null!;
    private MessageStoreManager _messageStoreManager = null!;

    [SetUp]
    public void Setup()
    {
        _mockClient = Substitute.For<IMqttManagedClient>();
        _mockLogger = Substitute.For<ILogger<MessageStoreManager>>();
        var config = new AppConfiguration();
        var mockPerformance = Substitute.For<IPerformanceSettings>();
        mockPerformance.Performance.Returns(config.Performance);
        var mockSparkplug = Substitute.For<ISparkplugSettings>();
        mockSparkplug.Sparkplug.Returns(new SparkplugSettings { EnrichAliasNames = true });
        _messageStoreManager = new MessageStoreManager(_mockClient, _mockLogger, mockPerformance,
            Substitute.For<IUxMetricsService>(), TestPipelineHelper.BuildBuiltInPipeline(), mockSparkplug);
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
        _messageStoreManager.RootTopicCount.Should().Be(0);
        _messageStoreManager.GetSelectedTopicState().FullTopic.Should().BeNull();
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
        var state = _messageStoreManager.GetSelectedTopicState();
        var result = await _messageStoreManager.GetSelectedMessagesAsync(state.Token, 10);
        result!.Messages.Should().BeEmpty();
    }

    [Test]
    public async Task GetMessagesForSelectedTopic_Should_ReturnMessages_WhenMessagesExist()
    {
        _messageStoreManager.AddMessage("selected/topic", new MqttMessage());
        _messageStoreManager.SelectTopic("selected/topic");
        var state = _messageStoreManager.GetSelectedTopicState();
        var result = await _messageStoreManager.GetSelectedMessagesAsync(state.Token, 10);
        result!.Messages.Should().ContainSingle();
    }

    [Test]
    public void MessageStores_Should_BeInitializedProperly()
    {
        _messageStoreManager.RootTopicCount.Should().Be(0);
    }

    [Test]
    public async Task ClearAllMessages_Should_ClearStoreAndSelection()
    {
        _messageStoreManager.AddMessage("root", new MqttMessage());
        _messageStoreManager.SelectTopic("root");
        var selection = _messageStoreManager.GetSelectedTopicState();

        await _messageStoreManager.ClearAllMessages();

        _messageStoreManager.RootTopicCount.Should().Be(0);
        _messageStoreManager.GetSelectedTopicState().FullTopic.Should().BeNull();
        (await _messageStoreManager.GetSelectedMessagesAsync(selection.Token, 10)).Should().BeNull();
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
        _messageStoreManager.AddMessage("root/child", new MqttMessage { Topic = "root/child", Payload = "x" });
        _messageStoreManager.SelectTopic("root");

        await _messageStoreManager.ClearAllMessages();
        var state = _messageStoreManager.GetSelectedTopicState();
        var result = await _messageStoreManager.GetSelectedMessagesAsync(state.Token, 10);

        result!.Messages.Should().BeEmpty();
    }

    [Test]
    public async Task ReadSelectedTopic_RespectsLimit()
    {
        for (int i = 0; i < 100; i++)
            _messageStoreManager.AddMessage("sensors/temp", new MqttMessage { DateTimeReceived = DateTime.UtcNow.AddSeconds(-i) });

        var result = await MessageStoreReads.ReadMessagesAsync(_messageStoreManager, "sensors/temp", 10);

        result.Should().HaveCount(10);
        result.Should().BeInDescendingOrder(m => m.DateTimeReceived);
    }

    [Test]
    public async Task ReadSelectedTopic_ReturnsAllWhenUnderLimit()
    {
        for (int i = 0; i < 3; i++)
            _messageStoreManager.AddMessage("sensors/humidity", new MqttMessage { DateTimeReceived = DateTime.UtcNow.AddSeconds(-i) });

        var result = await MessageStoreReads.ReadMessagesAsync(_messageStoreManager, "sensors/humidity", 10);

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
        _messageStoreManager.AddMessage("sensors/temp", new MqttMessage());
        _messageStoreManager.SelectTopic("sensors/temp");
        var before = _messageStoreManager.GetSelectedTopicState().Token.ContentVersion;
        _messageStoreManager.AddMessage("sensors/temp", new MqttMessage());
        var after = _messageStoreManager.GetSelectedTopicState().Token.ContentVersion;

        after.Should().BeGreaterThan(before);
    }

    [Test]
    public async Task ReadSelectedTopic_AggregatesDescendantMessagesForParentTopic()
    {
        var t1 = DateTime.UtcNow.AddSeconds(-10);
        var t2 = DateTime.UtcNow.AddSeconds(-5);
        var t3 = DateTime.UtcNow.AddSeconds(-1);

        _messageStoreManager.AddMessage("sensors/temp", new MqttMessage { DateTimeReceived = t1, Topic = "sensors/temp" });
        _messageStoreManager.AddMessage("sensors/temp", new MqttMessage { DateTimeReceived = t3, Topic = "sensors/temp" });
        _messageStoreManager.AddMessage("sensors/humidity", new MqttMessage { DateTimeReceived = t2, Topic = "sensors/humidity" });

        var result = await MessageStoreReads.ReadMessagesAsync(_messageStoreManager, "sensors", 10);

        result.Should().HaveCount(3);
        result.Should().BeInDescendingOrder(m => m.DateTimeReceived);
    }

    [Test]
    public async Task ReadSelectedTopic_ParentTopic_RespectsLimitAcrossDescendants()
    {
        for (int i = 0; i < 5; i++)
            _messageStoreManager.AddMessage("sensors/temp", new MqttMessage { DateTimeReceived = DateTime.UtcNow.AddSeconds(-i), Topic = "sensors/temp" });
        for (int i = 0; i < 5; i++)
            _messageStoreManager.AddMessage("sensors/humidity", new MqttMessage { DateTimeReceived = DateTime.UtcNow.AddSeconds(-i - 100), Topic = "sensors/humidity" });

        var result = await MessageStoreReads.ReadMessagesAsync(_messageStoreManager, "sensors", 3);

        result.Should().HaveCount(3);
        result.Should().BeInDescendingOrder(m => m.DateTimeReceived);
    }

    [Test]
    public void GetSelectedTopicVersion_IncrementsWhenDescendantMessagesChange()
    {
        _messageStoreManager.AddMessage("sensors/temp", new MqttMessage());
        _messageStoreManager.SelectTopic("sensors");
        var before = _messageStoreManager.GetSelectedTopicState().Token.ContentVersion;
        _messageStoreManager.AddMessage("sensors/temp", new MqttMessage());
        var after = _messageStoreManager.GetSelectedTopicState().Token.ContentVersion;

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

        var sensors = _messageStoreManager.GetTopicTreeSnapshot().Roots.Single();
        var temp = sensors.Children.Single(node => node.Topic == "temp");
        var room1 = temp.Children.Single(node => node.Topic == "room1");
        var humidity = sensors.Children.Single(node => node.Topic == "humidity");

        sensors.TopicCount.Should().Be(3);
        temp.TopicCount.Should().Be(1);
        room1.TopicCount.Should().Be(0);
        humidity.TopicCount.Should().Be(0);

        sensors.MessageCount.Should().Be(5);
        temp.MessageCount.Should().Be(3);
        room1.MessageCount.Should().Be(2);
        humidity.MessageCount.Should().Be(2);
    }

    [Test]
    public void AddMessage_LeadingSlash_NormalizesToCleanPath()
    {
        _messageStoreManager.AddMessage("/sensors/temp", new MqttMessage());

        var sensors = _messageStoreManager.GetTopicTreeSnapshot().Roots.Should()
            .ContainSingle(node => node.FullTopic == "sensors").Subject;
        sensors.Children.Should().ContainSingle(node => node.FullTopic == "sensors/temp");
        _messageStoreManager.RootTopicCount.Should().Be(1);
    }

    [Test]
    public void AddMessage_TrailingSlash_NormalizesToCleanPath()
    {
        _messageStoreManager.AddMessage("sensors/temp/", new MqttMessage());

        var temp = _messageStoreManager.GetTopicTreeSnapshot().Roots.Single().Children.Single();
        temp.FullTopic.Should().Be("sensors/temp");
        temp.MessageCount.Should().Be(1);
        temp.HasDirectMessages.Should().BeTrue();
    }

    [Test]
    public void AddMessage_DoubleSlash_NormalizesToCleanPath()
    {
        _messageStoreManager.AddMessage("a//b", new MqttMessage());

        var a = _messageStoreManager.GetTopicTreeSnapshot().Roots.Single();
        a.FullTopic.Should().Be("a");
        a.Children.Should().ContainSingle().Which.FullTopic.Should().Be("a/b");
    }

    [Test]
    public void AddMessage_AllSlashes_Rejected_StoreRemainsEmpty()
    {
        _messageStoreManager.AddMessage("///", new MqttMessage());

        _messageStoreManager.RootTopicCount.Should().Be(0);
    }

    [Test]
    public void AddMessage_CleanSparkplugTopic_Unchanged()
    {
        _messageStoreManager.AddMessage("spBv1.0/group/NBIRTH/node1", new MqttMessage());

        var node = _messageStoreManager.GetTopicTreeSnapshot().Roots.Single();
        foreach (var segment in new[] { "group", "NBIRTH", "node1" })
            node = node.Children.Single(child => child.Topic == segment);
        node.HasDirectMessages.Should().BeTrue();
    }

    [Test]
    public async Task AddMessage_LeadingSlash_PreservesOriginalTopicOnMessage()
    {
        var msg = new MqttMessage();
        _messageStoreManager.AddMessage("/sensors/temp", msg);

        _messageStoreManager.SelectTopic("sensors/temp");
        var selection = _messageStoreManager.GetSelectedTopicState();
        (await _messageStoreManager.GetSelectedMessagesAsync(selection.Token, 10))!
            .Messages.Should().ContainSingle().Which.Should().BeSameAs(msg);
    }
}
