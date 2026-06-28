using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using MQTTnet;
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
public class MessageStoreManagerMessageHandlerTests
{
    private IManagedMqttClient _mockClient = null!;
    private ILogger<MessageStoreManager> _mockLogger = null!;
    private MessageStoreManager _manager = null!;
    private Func<MqttApplicationMessageReceivedEventArgs, Task>? _capturedHandler;

    [SetUp]
    public async Task Setup()
    {
        _mockClient = Substitute.For<IManagedMqttClient>();
        _mockLogger = Substitute.For<ILogger<MessageStoreManager>>();
        var mockSettings = Substitute.For<ISettingsStore>();
        mockSettings.Config.Returns(new AppConfiguration());
        _manager = new MessageStoreManager(_mockClient, _mockLogger, mockSettings, Substitute.For<IUxTelemetryService>());

        _capturedHandler = null;
        _mockClient.When(x =>
            x.ApplicationMessageReceivedAsync +=
                Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>())
            .Do(x => _capturedHandler =
                x.Arg<Func<MqttApplicationMessageReceivedEventArgs, Task>>());

        await _manager.Start();
    }

    [TearDown]
    public void TearDown()
    {
        _manager.Dispose();
        _mockClient.Dispose();
    }

    private static MqttApplicationMessageReceivedEventArgs MakeArgs(string topic, string payload = "")
    {
        var appMsg = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .Build();
        var publishPacket = new MQTTnet.Packets.MqttPublishPacket { Topic = topic };
        return new MqttApplicationMessageReceivedEventArgs("test-client", appMsg, publishPacket, null);
    }

    private Task Fire(string topic, string payload = "") =>
        _capturedHandler!(MakeArgs(topic, payload));

    [Test]
    public async Task MessageReceived_PlainText_StoresInCorrectTopicNode()
    {
        await Fire("sensors", "42");

        _manager.MessageStores.Should().ContainKey("sensors");
        var msgs = _manager.MessageStores["sensors"].Messages;
        msgs.Should().NotBeNull();
        msgs!.Should().Contain(m => m.Payload == "42");
    }

    [Test]
    public async Task MessageReceived_ValidJson_StoresPayloadAsString()
    {
        await Fire("data", """{"temp":21.5}""");

        _manager.MessageStores["data"].Messages!
            .Should().Contain(m => m.Payload != null && m.Payload.Contains("temp"));
    }

    [Test]
    public async Task MessageReceived_EmptyPayload_StoresEmptyString_DoesNotThrow()
    {
        var act = async () => await Fire("empty/topic", "");
        await act.Should().NotThrowAsync();

        _manager.MessageStores.Should().ContainKey("empty");
    }

    [Test]
    public async Task MessageReceived_SingleTopic_CapsAtGlobalMaxStoredMessages()
    {
        // With a single active topic, that topic may hold the entire global budget.
        // Default MaxStoredMessages is 10_000.
        for (var i = 0; i < 10_010; i++)
            await Fire("capped", $"msg-{i}");

        _manager.MessageStores["capped"].Messages!.Count.Should().Be(10_000);
        _manager.TotalStoredMessages.Should().Be(10_000);
    }

    [Test]
    public async Task GetMessagesForSelectedTopic_ReturnsSortedNewestFirst()
    {
        var store = new MessageStore
        {
            Messages = new ConcurrentQueue<MqttMessage>()
        };
        var old = new MqttMessage("old", "t", false, MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce);
        await Task.Delay(5);
        var newest = new MqttMessage("new", "t", false, MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce);
        store.Messages.Enqueue(old);
        store.Messages.Enqueue(newest);
        _manager.SelectedMessageStore = store;

        var result = (await _manager.GetMessagesForSelectedTopic()).ToList();

        result[0].Payload.Should().Be("new");
        result[1].Payload.Should().Be("old");
    }

    [Test]
    public async Task GetMessagesForSelectedTopic_AggregatesChildTopics()
    {
        var child1 = new MessageStore { Messages = new ConcurrentQueue<MqttMessage>() };
        child1.Messages.Enqueue(new MqttMessage("from-child-1", "a/b", false, MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce));
        var child2 = new MessageStore { Messages = new ConcurrentQueue<MqttMessage>() };
        child2.Messages.Enqueue(new MqttMessage("from-child-2", "a/c", false, MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce));

        var parent = new MessageStore
        {
            SubTopics = new ConcurrentDictionary<string, MessageStore>()
        };
        parent.SubTopics.TryAdd("b", child1);
        parent.SubTopics.TryAdd("c", child2);
        _manager.SelectedMessageStore = parent;

        var result = (await _manager.GetMessagesForSelectedTopic()).ToList();

        result.Should().HaveCount(2);
        result.Should().Contain(m => m.Payload == "from-child-1");
        result.Should().Contain(m => m.Payload == "from-child-2");
    }

    [Test]
    public async Task MessageReceived_NestedTopic_BuildsCorrectTree()
    {
        await Fire("a/b/c", "deep");

        _manager.MessageStores.Should().ContainKey("a");
        var a = _manager.MessageStores["a"];
        a.SubTopics.Should().ContainKey("b");
        a.SubTopics!["b"].SubTopics.Should().ContainKey("c");
        a.SubTopics["b"].SubTopics!["c"].Messages!
            .Should().Contain(m => m.Payload == "deep");
    }

    [Test]
    public async Task MessageReceived_FirstMessageForNestedLeaf_IsStored()
    {
        await Fire("plant/area/line", "first");

        var leaf = _manager.MessageStores["plant"]
            .SubTopics!["area"]
            .SubTopics!["line"];

        leaf.Messages.Should().NotBeNull();
        leaf.Messages!.Should().ContainSingle(m => m.Payload == "first");
    }

    [Test]
    public async Task MessageReceived_SparkplugTopic_WithEmptyPayload_DoesNotThrow()
    {
        // An empty payload causes protobuf→JSON serialisation to fail.
        // The MessageHandler catch block should absorb the exception.
        var act = async () => await Fire("spBv1.0/group/NBIRTH/eon1", "");
        await act.Should().NotThrowAsync();
    }

    [Test]
    public async Task MessageReceived_SparkplugTopic_WithPlainJsonPayload_StoresPayload()
    {
        const string json = """{"timestamp":123,"metrics":[]}""";

        await Fire("spBv1.0/group/DDATA/eon1", json);

        var msgs = _manager.MessageStores["spBv1.0"].SubTopics!["group"]
            .SubTopics!["DDATA"].SubTopics!["eon1"].Messages;
        msgs.Should().NotBeNull();
        msgs!.Should().Contain(m => m.Payload == json);
    }

    [Test]
    public async Task MessageReceived_Event_FiresAfterMessageIsStored()
    {
        MqttMessage? received = null;
        _manager.MessageReceived += msg =>
        {
            received = msg;
            return Task.CompletedTask;
        };

        await Fire("event/test", "hello");

        received.Should().NotBeNull();
        received!.Payload.Should().Be("hello");
        received.Topic.Should().Be("event/test");
    }

    [Test]
    public async Task MessageReceived_FaultingHandler_DoesNotCrashPipeline()
    {
        _manager.MessageReceived += _ => throw new InvalidOperationException("handler fault");

        var act = async () => await Fire("fault/topic", "data");

        await act.Should().NotThrowAsync();
        _manager.MessageStores.Should().ContainKey("fault");
    }

    [Test]
    public async Task Start_CalledTwice_SubscribesOnce()
    {
        await _manager.Start(); // second call — already listening from [SetUp]
        _mockClient.Received(1).ApplicationMessageReceivedAsync +=
            Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>();
    }

    [Test]
    public async Task MessageHandler_RateLimit_ExcessMessagesDropped()
    {
        var rateLimitedLogger = Substitute.For<ILogger<MessageStoreManager>>();
        var rateLimitedClient = Substitute.For<IManagedMqttClient>();
        var mockSettings = Substitute.For<ISettingsStore>();
        mockSettings.Config.Returns(new AppConfiguration
        {
            Performance = new PerformanceSettings { MaxMessagesPerSecond = 1, MaxStoredMessages = 10_000 }
        });

        Func<MqttApplicationMessageReceivedEventArgs, Task>? handler = null;
        rateLimitedClient
            .When(x => x.ApplicationMessageReceivedAsync += Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>())
            .Do(x => handler = x.Arg<Func<MqttApplicationMessageReceivedEventArgs, Task>>());

        using var manager = new MessageStoreManager(rateLimitedClient, rateLimitedLogger, mockSettings, Substitute.For<IUxTelemetryService>());
        await manager.Start();

        // Fire 3 messages immediately — only the first should be permitted in the 1-second window.
        await handler!(MakeArgs("rl", "first"));
        await handler!(MakeArgs("rl", "second"));
        await handler!(MakeArgs("rl", "third"));

        manager.MessageStores["rl"].Messages!.Count.Should().Be(1);
    }

    [Test]
    public void MaxStoredMessages_UpdatesWhenSettingsChange()
    {
        var config = new AppConfiguration
        {
            Performance = new PerformanceSettings { MaxMessagesPerSecond = 1, MaxStoredMessages = 10_000 }
        };
        var mockSettings = Substitute.For<ISettingsStore>();
        mockSettings.Config.Returns(config);

        using var manager = new MessageStoreManager(Substitute.For<IManagedMqttClient>(),
            Substitute.For<ILogger<MessageStoreManager>>(), mockSettings, Substitute.For<IUxTelemetryService>());

        config.Performance.MaxStoredMessages = 25;

        manager.MaxStoredMessages.Should().Be(25,
            "MaxStoredMessages must be a live read of the current settings, not a snapshot from construction");
    }

    private static (MessageStoreManager Manager, Func<MqttApplicationMessageReceivedEventArgs, Task> Fire, ISettingsStore Settings)
        BuildManager(AppConfiguration config)
    {
        var client = Substitute.For<IManagedMqttClient>();
        Func<MqttApplicationMessageReceivedEventArgs, Task>? handler = null;
        client.When(x => x.ApplicationMessageReceivedAsync += Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>())
              .Do(x => handler = x.Arg<Func<MqttApplicationMessageReceivedEventArgs, Task>>());

        var settings = Substitute.For<ISettingsStore>();
        settings.Config.Returns(config);

        var manager = new MessageStoreManager(client, Substitute.For<ILogger<MessageStoreManager>>(),
            settings, Substitute.For<IUxTelemetryService>());
        manager.Start().GetAwaiter().GetResult();
        return (manager, handler!, settings);
    }

    private static int CountStored(IEnumerable<MessageStore> stores)
    {
        var total = 0;
        foreach (var s in stores)
        {
            total += s.Messages?.Count ?? 0;
            if (s.SubTopics != null)
                total += CountStored(s.SubTopics.Values);
        }
        return total;
    }

    [Test]
    public async Task Retention_GlobalCapAcrossManyTopics_TotalNeverExceedsLimit()
    {
        var config = new AppConfiguration
        {
            Performance = new PerformanceSettings { MaxStoredMessages = 5, MaxMessagesPerSecond = 50_000 }
        };
        var built = BuildManager(config);
        using var manager = built.Manager;
        var fire = built.Fire;

        // 12 messages across 12 distinct topics; global cap is 5.
        for (var i = 0; i < 12; i++)
            await fire(MakeArgs($"topic-{i}", $"msg-{i}"));

        manager.TotalStoredMessages.Should().Be(5);
        CountStored(manager.MessageStores.Values).Should().Be(5,
            "the live counter must match the messages actually retained in the tree");
    }

    [Test]
    public async Task Retention_GlobalFifo_EvictsOldestAcrossTopicBoundaries()
    {
        var config = new AppConfiguration
        {
            Performance = new PerformanceSettings { MaxStoredMessages = 3, MaxMessagesPerSecond = 50_000 }
        };
        var built = BuildManager(config);
        using var manager = built.Manager;
        var fire = built.Fire;

        // Arrival order: a:0, b:1, a:2, c:3  -> retentionOrder [a,b,a,c], cap 3.
        // Oldest overall (a's "0") is evicted; a keeps "2".
        await fire(MakeArgs("a", "0"));
        await fire(MakeArgs("b", "1"));
        await fire(MakeArgs("a", "2"));
        await fire(MakeArgs("c", "3"));

        manager.TotalStoredMessages.Should().Be(3);
        manager.MessageStores["a"].Messages!.Select(m => m.Payload).Should().ContainSingle().Which.Should().Be("2");
        manager.MessageStores["b"].Messages!.Should().ContainSingle(m => m.Payload == "1");
        manager.MessageStores["c"].Messages!.Should().ContainSingle(m => m.Payload == "3");
    }

    [Test]
    public async Task Retention_SingleHotTopic_RetainsExactlyTheGlobalLimit()
    {
        var config = new AppConfiguration
        {
            Performance = new PerformanceSettings { MaxStoredMessages = 4, MaxMessagesPerSecond = 50_000 }
        };
        var built = BuildManager(config);
        using var manager = built.Manager;
        var fire = built.Fire;

        for (var i = 0; i < 10; i++)
            await fire(MakeArgs("hot", $"msg-{i}"));

        // Per-topic cap is gone: one busy topic may use the whole global budget.
        manager.TotalStoredMessages.Should().Be(4);
        manager.MessageStores["hot"].Messages!.Count.Should().Be(4);
        manager.MessageStores["hot"].Messages!.Select(m => m.Payload)
            .Should().BeEquivalentTo(["msg-6", "msg-7", "msg-8", "msg-9"]);
    }

    [Test]
    public async Task Retention_NonPositiveLimit_StoresNothing_DoesNotSpin()
    {
        var config = new AppConfiguration
        {
            Performance = new PerformanceSettings { MaxStoredMessages = 0, MaxMessagesPerSecond = 50_000 }
        };
        var built = BuildManager(config);
        using var manager = built.Manager;
        var fire = built.Fire;

        await fire(MakeArgs("zero", "x"));
        await fire(MakeArgs("zero", "y"));

        manager.TotalStoredMessages.Should().Be(0);
        CountStored(manager.MessageStores.Values).Should().Be(0);
    }

    [Test]
    public async Task PerformanceSettingsChanged_RebuildsRateLimiter()
    {
        var rateLimitedLogger = Substitute.For<ILogger<MessageStoreManager>>();
        var rateLimitedClient = Substitute.For<IManagedMqttClient>();
        var config = new AppConfiguration
        {
            Performance = new PerformanceSettings { MaxMessagesPerSecond = 1, MaxStoredMessages = 10_000 }
        };
        var mockSettings = Substitute.For<ISettingsStore>();
        mockSettings.Config.Returns(config);

        Func<MqttApplicationMessageReceivedEventArgs, Task>? handler = null;
        rateLimitedClient
            .When(x => x.ApplicationMessageReceivedAsync += Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>())
            .Do(x => handler = x.Arg<Func<MqttApplicationMessageReceivedEventArgs, Task>>());

        using var manager = new MessageStoreManager(rateLimitedClient, rateLimitedLogger, mockSettings, Substitute.For<IUxTelemetryService>());
        await manager.Start();

        await handler!(MakeArgs("before", "x"));
        await handler!(MakeArgs("before", "y"));
        await handler!(MakeArgs("before", "z"));
        manager.MessageStores["before"].Messages!.Count.Should().Be(1);

        config.Performance.MaxMessagesPerSecond = 10_000;
        mockSettings.PerformanceSettingsChanged += Raise.Event<Action>();

        await handler!(MakeArgs("after", "1"));
        await handler!(MakeArgs("after", "2"));
        await handler!(MakeArgs("after", "3"));
        manager.MessageStores["after"].Messages!.Count.Should().Be(3);
    }
}
