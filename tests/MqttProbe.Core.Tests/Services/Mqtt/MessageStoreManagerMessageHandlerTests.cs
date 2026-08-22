using System.Collections.Concurrent;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Models.Sparkplug;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Metrics;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Core.Services.Sparkplug;
using MqttProbe.PluginContracts;
using MqttProbe.Tests.Utilities;
using Org.Eclipse.Tahu.Protobuf;

namespace MqttProbe.Core.Tests.Services.Mqtt;

[TestFixture]
public class MessageStoreManagerMessageHandlerTests
{
    private IMqttManagedClient _mockClient = null!;
    private ILogger<MessageStoreManager> _mockLogger = null!;
    private MessageStoreManager _manager = null!;
    private Func<MqttApplicationMessageReceivedEventArgs, Task>? _capturedHandler;

    [SetUp]
    public async Task Setup()
    {
        _mockClient = Substitute.For<IMqttManagedClient>();
        _mockLogger = Substitute.For<ILogger<MessageStoreManager>>();
        var config = new AppConfiguration();
        var mockPerformance = Substitute.For<IPerformanceSettings>();
        mockPerformance.Performance.Returns(config.Performance);
        var mockSparkplug = Substitute.For<ISparkplugSettings>();
        mockSparkplug.Sparkplug.Returns(new SparkplugSettings { EnrichAliasNames = true });
        _manager = new MessageStoreManager(_mockClient, _mockLogger, mockPerformance,
            Substitute.For<IUxMetricsService>(), TestPipelineHelper.BuildBuiltInPipeline(), mockSparkplug);

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
        await Fire("sensors", "42 degrees");

        _manager.MessageStores.Should().ContainKey("sensors");
        var msgs = _manager.MessageStores["sensors"].Messages;
        msgs.Should().NotBeNull();
        msgs.Should().Contain(m => m.Payload == "42 degrees");
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
        var act = async () => await Fire("empty/topic");
        await act.Should().NotThrowAsync();

        _manager.MessageStores.Should().ContainKey("empty");
    }

    [Test]
    public async Task MessageReceived_SingleTopic_CapsAtGlobalMaxStoredMessages()
    {
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
        await Fire("a/b/c", "deep value");

        _manager.MessageStores.Should().ContainKey("a");
        var a = _manager.MessageStores["a"];
        a.SubTopics.Should().ContainKey("b");
        a.SubTopics!["b"].SubTopics.Should().ContainKey("c");
        a.SubTopics["b"].SubTopics!["c"].Messages!
            .Should().Contain(m => m.Payload == "deep value");
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
        var act = async () => await Fire("spBv1.0/group/NBIRTH/eon1");
        await act.Should().NotThrowAsync();
    }

    [Test]
    public async Task MessageReceived_SparkplugTopic_WithPlainJsonPayload_StoresAsJson()
    {
        const string json = """{"timestamp":123,"metrics":[]}""";

        await Fire("spBv1.0/group/DDATA/eon1", json);

        var msgs = _manager.MessageStores["spBv1.0"].SubTopics!["group"]
            .SubTopics!["DDATA"].SubTopics!["eon1"].Messages;
        msgs.Should().NotBeNull();
        msgs.Should().ContainSingle(m => m.Payload == json && m.FormatId == "json");
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
        await _manager.Start();
        _mockClient.Received(1).ApplicationMessageReceivedAsync +=
            Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>();
    }

    [Test]
    public async Task MessageHandler_RateLimit_ExcessMessagesDropped()
    {
        var rateLimitedLogger = Substitute.For<ILogger<MessageStoreManager>>();
        var rateLimitedClient = Substitute.For<IMqttManagedClient>();
        var config = new AppConfiguration
        {
            Performance = new PerformanceSettings { MaxMessagesPerSecond = 1, MaxStoredMessages = 10_000 }
        };
        var mockPerformance = Substitute.For<IPerformanceSettings>();
        mockPerformance.Performance.Returns(config.Performance);
        var mockSparkplug = Substitute.For<ISparkplugSettings>();
        mockSparkplug.Sparkplug.Returns(new SparkplugSettings { EnrichAliasNames = true });

        Func<MqttApplicationMessageReceivedEventArgs, Task>? handler = null;
        rateLimitedClient
            .When(x => x.ApplicationMessageReceivedAsync += Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>())
            .Do(x => handler = x.Arg<Func<MqttApplicationMessageReceivedEventArgs, Task>>());

        using var manager = new MessageStoreManager(rateLimitedClient, rateLimitedLogger, mockPerformance,
            Substitute.For<IUxMetricsService>(), TestPipelineHelper.BuildBuiltInPipeline(), mockSparkplug);
        await manager.Start();

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
        var mockPerformance = Substitute.For<IPerformanceSettings>();
        mockPerformance.Performance.Returns(config.Performance);
        var mockSparkplug2 = Substitute.For<ISparkplugSettings>();
        mockSparkplug2.Sparkplug.Returns(new SparkplugSettings { EnrichAliasNames = true });

        using var manager = new MessageStoreManager(Substitute.For<IMqttManagedClient>(),
            Substitute.For<ILogger<MessageStoreManager>>(), mockPerformance,
            Substitute.For<IUxMetricsService>(), TestPipelineHelper.BuildBuiltInPipeline(), mockSparkplug2);

        config.Performance.MaxStoredMessages = 25;

        manager.MaxStoredMessages.Should().Be(25,
            "MaxStoredMessages must be a live read of the current settings, not a snapshot from construction");
    }

    private static (MessageStoreManager Manager, Func<MqttApplicationMessageReceivedEventArgs, Task> Fire, IPerformanceSettings Settings)
        BuildManager(AppConfiguration config)
    {
        var client = Substitute.For<IMqttManagedClient>();
        Func<MqttApplicationMessageReceivedEventArgs, Task>? handler = null;
        client.When(x => x.ApplicationMessageReceivedAsync += Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>())
              .Do(x => handler = x.Arg<Func<MqttApplicationMessageReceivedEventArgs, Task>>());

        var performanceSettings = Substitute.For<IPerformanceSettings>();
        performanceSettings.Performance.Returns(config.Performance);
        var sparkplugSettings = Substitute.For<ISparkplugSettings>();
        sparkplugSettings.Sparkplug.Returns(new SparkplugSettings { EnrichAliasNames = true });

        var manager = new MessageStoreManager(client, Substitute.For<ILogger<MessageStoreManager>>(),
            performanceSettings, Substitute.For<IUxMetricsService>(), TestPipelineHelper.BuildBuiltInPipeline(), sparkplugSettings);
        manager.Start().GetAwaiter().GetResult();
        return (manager, handler!, performanceSettings);
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

        manager.TotalStoredMessages.Should().Be(4);
        manager.MessageStores["hot"].Messages!.Count.Should().Be(4);
        manager.MessageStores["hot"].Messages!.Select(m => m.Payload)
            .Should().BeEquivalentTo("msg-6", "msg-7", "msg-8", "msg-9");
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
    public async Task MessageHandler_InvokedAfterDispose_DropsQuietly()
    {
        var config = new AppConfiguration
        {
            Performance = new PerformanceSettings { MaxStoredMessages = 10, MaxMessagesPerSecond = 50_000 }
        };
        var built = BuildManager(config);
        var manager = built.Manager;
        var fire = built.Fire;

        manager.Dispose();

        var act = async () => await fire(MakeArgs("after-dispose", "x"));

        await act.Should().NotThrowAsync(
            "a message already in flight can reach the handler after disposal, and the "
            + "MQTT client's handler must not see ObjectDisposedException");
        manager.MessageStores.Should().NotContainKey("after-dispose");
    }

    [Test]
    public void Dispose_CalledTwice_TearsDownOnce()
    {
        var config = new AppConfiguration();
        var built = BuildManager(config);
        var manager = built.Manager;
        var settings = built.Settings;

        manager.Dispose();
        var act = manager.Dispose;

        act.Should().NotThrow();
        settings.Received(1).PerformanceSettingsChanged -= Arg.Any<Action>();
    }

    [Test]
    public async Task ClearAllMessages_ResetsGlobalCount_SoNewMessagesStore()
    {
        var config = new AppConfiguration
        {
            Performance = new PerformanceSettings { MaxStoredMessages = 5, MaxMessagesPerSecond = 50_000 }
        };
        var built = BuildManager(config);
        using var manager = built.Manager;
        var fire = built.Fire;

        for (var i = 0; i < 5; i++)
            await fire(MakeArgs($"t-{i}", "x"));
        manager.TotalStoredMessages.Should().Be(5);

        await manager.ClearAllMessages();

        manager.TotalStoredMessages.Should().Be(0);
        manager.MessageStores.Should().BeEmpty();

        await fire(MakeArgs("after-clear", "y"));
        manager.TotalStoredMessages.Should().Be(1);
        CountStored(manager.MessageStores.Values).Should().Be(1);
    }

    [Test]
    public async Task PerformanceSettingsChanged_LoweredLimit_TrimsImmediately()
    {
        var config = new AppConfiguration
        {
            Performance = new PerformanceSettings { MaxStoredMessages = 10, MaxMessagesPerSecond = 50_000 }
        };
        var built = BuildManager(config);
        using var manager = built.Manager;
        var fire = built.Fire;
        var settings = built.Settings;

        for (var i = 0; i < 8; i++)
            await fire(MakeArgs($"t-{i}", "x"));
        manager.TotalStoredMessages.Should().Be(8);

        config.Performance.MaxStoredMessages = 3;
        settings.PerformanceSettingsChanged += Raise.Event<Action>();

        manager.TotalStoredMessages.Should().Be(3);
        CountStored(manager.MessageStores.Values).Should().Be(3);
    }

    [Test]
    public async Task MessageReceived_ValidJson_SetsFormatIdToJson()
    {
        await Fire("fmt/test", """{"temp":21.5}""");

        _manager.MessageStores["fmt"].SubTopics!["test"].Messages!
            .Should().ContainSingle(m => m.FormatId == "json");
    }

    [Test]
    public async Task MessageReceived_EmptyPayload_SetsFormatIdToEmpty()
    {
        await Fire("fmt/empty");

        _manager.MessageStores["fmt"].SubTopics!["empty"].Messages!
            .Should().ContainSingle(m => m.FormatId == "empty");
    }

    [Test]
    public async Task MessageReceived_PlainText_SetsFormatIdToPlaintext()
    {
        await Fire("fmt/plain", "hello world");

        _manager.MessageStores["fmt"].SubTopics!["plain"].Messages!
            .Should().ContainSingle(m => m.FormatId == "plaintext");
    }

    [Test]
    public async Task PerformanceSettingsChanged_RebuildsRateLimiter()
    {
        var rateLimitedLogger = Substitute.For<ILogger<MessageStoreManager>>();
        var rateLimitedClient = Substitute.For<IMqttManagedClient>();
        var config = new AppConfiguration
        {
            Performance = new PerformanceSettings { MaxMessagesPerSecond = 1, MaxStoredMessages = 10_000 }
        };
        var mockPerformance = Substitute.For<IPerformanceSettings>();
        mockPerformance.Performance.Returns(config.Performance);
        var mockSparkplug = Substitute.For<ISparkplugSettings>();
        mockSparkplug.Sparkplug.Returns(new SparkplugSettings { EnrichAliasNames = true });

        Func<MqttApplicationMessageReceivedEventArgs, Task>? handler = null;
        rateLimitedClient
            .When(x => x.ApplicationMessageReceivedAsync += Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>())
            .Do(x => handler = x.Arg<Func<MqttApplicationMessageReceivedEventArgs, Task>>());

        using var manager = new MessageStoreManager(rateLimitedClient, rateLimitedLogger, mockPerformance,
            Substitute.For<IUxMetricsService>(), TestPipelineHelper.BuildBuiltInPipeline(), mockSparkplug);
        await manager.Start();

        await handler!(MakeArgs("before", "x"));
        await handler!(MakeArgs("before", "y"));
        await handler!(MakeArgs("before", "z"));
        manager.MessageStores["before"].Messages!.Count.Should().Be(1);

        config.Performance.MaxMessagesPerSecond = 10_000;
        mockPerformance.PerformanceSettingsChanged += Raise.Event<Action>();

        await handler!(MakeArgs("after", "1"));
        await handler!(MakeArgs("after", "2"));
        await handler!(MakeArgs("after", "3"));
        manager.MessageStores["after"].Messages!.Count.Should().Be(3);
    }

    [Test]
    public async Task TopicExclusion_PurgesExistingDataAndGatesFutureMessages()
    {
        var client = Substitute.For<IMqttManagedClient>();
        Func<MqttApplicationMessageReceivedEventArgs, Task>? handler = null;
        client.When(x => x.ApplicationMessageReceivedAsync += Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>())
            .Do(x => handler = x.Arg<Func<MqttApplicationMessageReceivedEventArgs, Task>>());

        var session = new SessionState { SelectedConnection = new Connection() };
        var excludes = new TopicExcludeService(
            Substitute.For<ILogger<TopicExcludeService>>(),
            Substitute.For<IConnectionSettings>(), session);
        var metrics = Substitute.For<IUxMetricsService>();
        var performance = Substitute.For<IPerformanceSettings>();
        performance.Performance.Returns(new AppConfiguration().Performance);
        var sparkplug = Substitute.For<ISparkplugSettings>();
        sparkplug.Sparkplug.Returns(new SparkplugSettings { EnrichAliasNames = true });

        using var manager = new MessageStoreManager(client, Substitute.For<ILogger<MessageStoreManager>>(),
            performance, metrics, TestPipelineHelper.BuildBuiltInPipeline(), sparkplug,
            topicExcludeService: excludes);
        await manager.Start();
        await handler!(MakeArgs("sensors/temp", "before"));

        await excludes.Add("sensors/#");
        manager.MessageStores.Should().BeEmpty();

        await handler!(MakeArgs("sensors/temp", "after"));

        manager.MessageStores.Should().BeEmpty();
        metrics.Received(1).RecordMessageExcluded();
        excludes.Dispose();
    }

    [Test]
    public async Task TopicExclusion_PurgesEmptyRetentionNodes()
    {
        var client = Substitute.For<IMqttManagedClient>();
        Func<MqttApplicationMessageReceivedEventArgs, Task>? handler = null;
        client.When(x => x.ApplicationMessageReceivedAsync += Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>())
            .Do(x => handler = x.Arg<Func<MqttApplicationMessageReceivedEventArgs, Task>>());

        var session = new SessionState { SelectedConnection = new Connection() };
        var excludes = new TopicExcludeService(
            Substitute.For<ILogger<TopicExcludeService>>(),
            Substitute.For<IConnectionSettings>(), session);
        var performance = Substitute.For<IPerformanceSettings>();
        performance.Performance.Returns(new AppConfiguration
        {
            Performance = new PerformanceSettings { MaxStoredMessages = 0, MaxMessagesPerSecond = 50_000 }
        }.Performance);
        var sparkplug = Substitute.For<ISparkplugSettings>();
        sparkplug.Sparkplug.Returns(new SparkplugSettings { EnrichAliasNames = true });

        using var manager = new MessageStoreManager(client, Substitute.For<ILogger<MessageStoreManager>>(),
            performance, Substitute.For<IUxMetricsService>(), TestPipelineHelper.BuildBuiltInPipeline(), sparkplug,
            topicExcludeService: excludes);
        await manager.Start();
        await handler!(MakeArgs("empty/topic", "payload"));
        manager.MessageStores.Should().ContainKey("empty");

        await excludes.Add("empty/#");

        manager.MessageStores.Should().BeEmpty();
        manager.TopicNodeCount.Should().Be(0);
        excludes.Dispose();
    }

    private static byte[] SparkplugPayload()
    {
        var payload = new Payload { Timestamp = 1 };
        payload.Metrics.Add(new Payload.Types.Metric
        {
            Name = "temperature",
            Alias = 7,
            Datatype = 3,
            IntValue = 42
        });
        return payload.ToByteArray();
    }

    private static MqttApplicationMessageReceivedEventArgs MakeBinaryArgs(string topic, byte[] payload)
    {
        var appMsg = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .Build();
        var publishPacket = new MQTTnet.Packets.MqttPublishPacket { Topic = topic };
        return new MqttApplicationMessageReceivedEventArgs("test-client", appMsg, publishPacket, null);
    }

    private static (MessageStoreManager Manager, Func<MqttApplicationMessageReceivedEventArgs, Task> Fire)
        BuildManagerWithTopology(IUxMetricsService metrics, ISparkplugTopologyService topology,
            ISparkplugCommandService? commandService = null)
    {
        var client = Substitute.For<IMqttManagedClient>();
        Func<MqttApplicationMessageReceivedEventArgs, Task>? handler = null;
        client.When(x => x.ApplicationMessageReceivedAsync += Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>())
              .Do(x => handler = x.Arg<Func<MqttApplicationMessageReceivedEventArgs, Task>>());

        var config = new AppConfiguration();
        var performanceSettings = Substitute.For<IPerformanceSettings>();
        performanceSettings.Performance.Returns(config.Performance);
        var sparkplugSettings = Substitute.For<ISparkplugSettings>();
        sparkplugSettings.Sparkplug.Returns(new SparkplugSettings { EnrichAliasNames = true });

        var manager = new MessageStoreManager(client, Substitute.For<ILogger<MessageStoreManager>>(),
            performanceSettings, metrics, TestPipelineHelper.BuildBuiltInPipeline(), sparkplugSettings, topology, commandService);
        manager.Start().GetAwaiter().GetResult();
        return (manager, handler!);
    }

    [Test]
    public async Task MessageHandler_TopologyApplyThrows_StillRecordsProcessedFormat()
    {
        var metrics = Substitute.For<IUxMetricsService>();
        var topology = Substitute.For<ISparkplugTopologyService>();
        topology.ApplyTopologyEventsAsync(Arg.Any<IReadOnlyList<TopologyEvent>>())
            .Returns(_ => throw new InvalidOperationException("topology fault"));

        var built = BuildManagerWithTopology(metrics, topology);
        using var manager = built.Manager;
        MqttMessage? received = null;
        manager.MessageReceived += msg => { received = msg; return Task.CompletedTask; };

        await built.Fire(MakeBinaryArgs("spBv1.0/g/NBIRTH/n1", SparkplugPayload()));

        await topology.Received(1).ApplyTopologyEventsAsync(Arg.Any<IReadOnlyList<TopologyEvent>>());
        metrics.Received(1).RecordMessageProcessed("sparkplug-b");
        received.Should().BeNull("the message is only constructed after topology events are applied");
    }

    [Test]
    public async Task MessageHandler_AliasEnrichment_ReadsTopologyOnlyAfterEventsApplied()
    {
        var topology = Substitute.For<ISparkplugTopologyService>();
        topology.Groups.Returns(new Dictionary<string, SpbGroup>());

        var built = BuildManagerWithTopology(Substitute.For<IUxMetricsService>(), topology);
        using var manager = built.Manager;

        await built.Fire(MakeBinaryArgs("spBv1.0/g/NBIRTH/n1", SparkplugPayload()));

        Received.InOrder(() =>
        {
            topology.ApplyTopologyEventsAsync(Arg.Any<IReadOnlyList<TopologyEvent>>());
            _ = topology.Groups;
        });
    }

    [Test]
    public async Task MessageHandler_NDataEvents_CallsCommandServiceAutoRebirth()
    {
        var metrics = Substitute.For<IUxMetricsService>();
        var topology = Substitute.For<ISparkplugTopologyService>();
        topology.Groups.Returns(new Dictionary<string, SpbGroup>());
        var commandService = Substitute.For<ISparkplugCommandService>();

        var built = BuildManagerWithTopology(metrics, topology, commandService);
        using var manager = built.Manager;

        await built.Fire(MakeBinaryArgs("spBv1.0/g/NDATA/n1", SparkplugPayload()));

        await commandService.Received(1).RequestNodeRebirthIfNeededAsync("g", "n1");
    }

    [Test]
    public async Task MessageHandler_DeviceDataEvents_CallsCommandServiceAutoRebirth()
    {
        var metrics = Substitute.For<IUxMetricsService>();
        var topology = Substitute.For<ISparkplugTopologyService>();
        topology.Groups.Returns(new Dictionary<string, SpbGroup>());
        var commandService = Substitute.For<ISparkplugCommandService>();

        var built = BuildManagerWithTopology(metrics, topology, commandService);
        using var manager = built.Manager;

        // Device data topic
        var payload = new Payload { Timestamp = 1 };
        payload.Metrics.Add(new Payload.Types.Metric
        {
            Name = "temperature",
            Datatype = 3,
            IntValue = 42
        });
        await built.Fire(MakeBinaryArgs("spBv1.0/g/DDATA/n1/d1", payload.ToByteArray()));

        await commandService.Received(1).RequestNodeRebirthIfNeededAsync("g", "n1");
    }
}
