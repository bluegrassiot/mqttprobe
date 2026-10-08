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
using MqttProbe.TestInfrastructure;
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

        (await MessageStoreReads.ReadMessagesAsync(_manager, "sensors", 10))
            .Should().ContainSingle(m => m.Payload == "42 degrees");
    }

    [Test]
    public async Task MessageReceived_ValidJson_StoresPayloadAsString()
    {
        await Fire("data", """{"temp":21.5}""");

        (await MessageStoreReads.ReadMessagesAsync(_manager, "data", 10))
            .Should().ContainSingle(m => m.Payload != null && m.Payload.Contains("temp"));
    }

    [Test]
    public async Task MessageReceived_EmptyPayload_StoresEmptyString_DoesNotThrow()
    {
        var act = async () => await Fire("empty/topic");
        await act.Should().NotThrowAsync();

        (await MessageStoreReads.ReadMessagesAsync(_manager, "empty/topic", 10)).Should().ContainSingle();
    }

    [Test]
    public async Task MessageReceived_SingleTopic_CapsAtGlobalMaxStoredMessages()
    {
        for (var i = 0; i < 10_010; i++)
            await Fire("capped", $"msg-{i}");

        (await MessageStoreReads.ReadMessagesAsync(_manager, "capped", 10_000)).Should().HaveCount(10_000);
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
        _manager.AddMessage("t", old);
        _manager.AddMessage("t", newest);
        _manager.SelectTopic("t");
        var selection = _manager.GetSelectedTopicState();

        var result = (await _manager.GetSelectedMessagesAsync(selection.Token, 10))!.Messages;

        result[0].Payload.Should().Be("new");
        result[1].Payload.Should().Be("old");
    }

    [Test]
    public async Task GetMessagesForSelectedTopic_AggregatesChildTopics()
    {
        _manager.AddMessage("a/b", new MqttMessage("from-child-1", "a/b", false, MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce));
        _manager.AddMessage("a/c", new MqttMessage("from-child-2", "a/c", false, MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce));
        _manager.SelectTopic("a");
        var selection = _manager.GetSelectedTopicState();
        var result = (await _manager.GetSelectedMessagesAsync(selection.Token, 10))!.Messages;

        result.Should().HaveCount(2);
        result.Should().Contain(m => m.Payload == "from-child-1");
        result.Should().Contain(m => m.Payload == "from-child-2");
    }

    [Test]
    public async Task MessageReceived_NestedTopic_BuildsCorrectTree()
    {
        await Fire("a/b/c", "deep value");

        var a = _manager.GetTopicTreeSnapshot().Roots.Should().ContainSingle(node => node.Topic == "a").Subject;
        var b = a.Children.Should().ContainSingle(node => node.Topic == "b").Subject;
        b.Children.Should().ContainSingle(node => node.Topic == "c");
        (await MessageStoreReads.ReadMessagesAsync(_manager, "a/b/c", 10)).Should().ContainSingle(m => m.Payload == "deep value");
    }

    [Test]
    public async Task MessageReceived_FirstMessageForNestedLeaf_IsStored()
    {
        await Fire("plant/area/line", "first");

        (await MessageStoreReads.ReadMessagesAsync(_manager, "plant/area/line", 10))
            .Should().ContainSingle(m => m.Payload == "first");
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

        (await MessageStoreReads.ReadMessagesAsync(_manager, "spBv1.0/group/DDATA/eon1", 10))
            .Should().ContainSingle(m => m.Payload == json && m.FormatId == "json");
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
        (await MessageStoreReads.ReadMessagesAsync(_manager, "fault/topic", 10)).Should().ContainSingle();
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

        (await MessageStoreReads.ReadMessagesAsync(manager, "rl", 10)).Should().ContainSingle();
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
        manager.GetTopicTreeSnapshot().Roots.Sum(node => node.MessageCount).Should().Be(5,
            "the live counter must match the messages actually retained in the tree");
        var actualRetained = 0;
        for (var i = 0; i < 12; i++)
            actualRetained += (await MessageStoreReads.ReadMessagesAsync(manager, $"topic-{i}", 20)).Count;
        actualRetained.Should().Be(5);
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
        (await MessageStoreReads.ReadMessagesAsync(manager, "a", 10)).Should().ContainSingle().Which.Payload.Should().Be("2");
        (await MessageStoreReads.ReadMessagesAsync(manager, "b", 10)).Should().ContainSingle(m => m.Payload == "1");
        (await MessageStoreReads.ReadMessagesAsync(manager, "c", 10)).Should().ContainSingle(m => m.Payload == "3");
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
        (await MessageStoreReads.ReadMessagesAsync(manager, "hot", 10)).Select(m => m.Payload)
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
        manager.GetTopicTreeSnapshot().Roots.Sum(node => node.MessageCount).Should().Be(0);
        (await MessageStoreReads.ReadMessagesAsync(manager, "zero", 10)).Should().BeEmpty();
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
        manager.RootTopicCount.Should().Be(0);
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
        manager.RootTopicCount.Should().Be(0);

        await fire(MakeArgs("after-clear", "y"));
        manager.TotalStoredMessages.Should().Be(1);
        manager.GetTopicTreeSnapshot().Roots.Sum(node => node.MessageCount).Should().Be(1);
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
        manager.GetTopicTreeSnapshot().Roots.Sum(node => node.MessageCount).Should().Be(3);
        for (var i = 0; i < 5; i++)
            (await MessageStoreReads.ReadMessagesAsync(manager, $"t-{i}", 10)).Should().BeEmpty();
        for (var i = 5; i < 8; i++)
            (await MessageStoreReads.ReadMessagesAsync(manager, $"t-{i}", 10)).Should().ContainSingle()
                .Which.Topic.Should().Be($"t-{i}");
    }

    [Test]
    public async Task MessageReceived_ValidJson_SetsFormatIdToJson()
    {
        await Fire("fmt/test", """{"temp":21.5}""");

        (await MessageStoreReads.ReadMessagesAsync(_manager, "fmt/test", 10))
            .Should().ContainSingle(m => m.FormatId == "json");
    }

    [Test]
    public async Task MessageReceived_EmptyPayload_SetsFormatIdToEmpty()
    {
        await Fire("fmt/empty");

        (await MessageStoreReads.ReadMessagesAsync(_manager, "fmt/empty", 10))
            .Should().ContainSingle(m => m.FormatId == "empty");
    }

    [Test]
    public async Task MessageReceived_PlainText_SetsFormatIdToPlaintext()
    {
        await Fire("fmt/plain", "hello world");

        (await MessageStoreReads.ReadMessagesAsync(_manager, "fmt/plain", 10))
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
        (await MessageStoreReads.ReadMessagesAsync(manager, "before", 10)).Should().ContainSingle();

        config.Performance.MaxMessagesPerSecond = 10_000;
        mockPerformance.PerformanceSettingsChanged += Raise.Event<Action>();

        await handler!(MakeArgs("after", "1"));
        await handler!(MakeArgs("after", "2"));
        await handler!(MakeArgs("after", "3"));
        (await MessageStoreReads.ReadMessagesAsync(manager, "after", 10)).Should().HaveCount(3);
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
        manager.RootTopicCount.Should().Be(0);

        await handler!(MakeArgs("sensors/temp", "after"));

        manager.RootTopicCount.Should().Be(0);
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
        manager.RootTopicCount.Should().Be(1);

        await excludes.Add("empty/#");

        manager.RootTopicCount.Should().Be(0);
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
            ISparkplugCommandService? commandService = null, ITopicExcludeService? excludes = null)
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
            performanceSettings, metrics, TestPipelineHelper.BuildBuiltInPipeline(), sparkplugSettings,
            topology, commandService, excludes);
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

    [Test]
    public async Task TopicExclusion_AddWaitsForInFlightMessage_ThenPurgesStoreAndTopology()
    {
        var client = Substitute.For<IMqttManagedClient>();
        Func<MqttApplicationMessageReceivedEventArgs, Task>? handler = null;
        client.When(x => x.ApplicationMessageReceivedAsync += Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>())
            .Do(x => handler = x.Arg<Func<MqttApplicationMessageReceivedEventArgs, Task>>());

        var session = new SessionState { SelectedConnection = new Connection() };
        using var excludes = new TopicExcludeService(
            Substitute.For<ILogger<TopicExcludeService>>(),
            Substitute.For<IConnectionSettings>(), session);
        var performance = Substitute.For<IPerformanceSettings>();
        performance.Performance.Returns(new AppConfiguration().Performance);
        var sparkplug = Substitute.For<ISparkplugSettings>();
        sparkplug.Sparkplug.Returns(new SparkplugSettings { EnrichAliasNames = true });
        var topology = new BarrierTopologyService();
        MqttMessage? received = null;

        using var manager = new MessageStoreManager(client, Substitute.For<ILogger<MessageStoreManager>>(),
            performance, Substitute.For<IUxMetricsService>(), TestPipelineHelper.BuildBuiltInPipeline(),
            sparkplug, topology, topicExcludeService: excludes);
        manager.MessageReceived += msg =>
        {
            received = msg;
            return Task.CompletedTask;
        };
        await manager.Start();

        const string topic = "spBv1.0/g/NBIRTH/n1";
        var fireTask = handler!(MakeBinaryArgs(topic, SparkplugPayload()));
        Task<TopicExcludeOperationResult>? addTask = null;
        var filterApplied = false;
        var finishedWhileParked = false;
        try
        {
            (await SatisfiesAsync(() => topology.Reached, TimeSpan.FromSeconds(5))).Should().BeTrue(
                "the message has to be parked inside the exclusion read gate before Add starts");
            addTask = excludes.Add("spBv1.0/#");
            filterApplied = await SatisfiesAsync(() => excludes.IsExcluded(topic), TimeSpan.FromSeconds(2));
            finishedWhileParked = await SatisfiesAsync(
                () => addTask.IsCompleted, TimeSpan.FromMilliseconds(250));
        }
        finally
        {
            topology.Release();
        }

        var result = await addTask.WaitAsync(TimeSpan.FromSeconds(10));
        await fireTask.WaitAsync(TimeSpan.FromSeconds(10));

        filterApplied.Should().BeTrue("the filter has to be live before the purge waits for readers");
        finishedWhileParked.Should().BeFalse(
            "Add must not purge while the message is still in flight, or the insert lands after the purge");
        result.IsValid.Should().BeTrue();
        received.Should().NotBeNull("the in-flight message is stored first and purged after, not dropped");
        manager.RootTopicCount.Should().Be(0);
        topology.Groups.Should().BeEmpty(
            "topology written while the message was in flight has to be purged as well");
    }

    [Test]
    public async Task TopicExclusion_ReloadDuringInFlightMessage_PurgesAfterTheCommit()
    {
        var session = new SessionState { SelectedConnection = new Connection() };
        using var excludes = new TopicExcludeService(
            Substitute.For<ILogger<TopicExcludeService>>(),
            Substitute.For<IConnectionSettings>(), session);
        var published = new List<string>();
        excludes.TopicExcluded += published.Add;
        var topology = new BarrierTopologyService();
        var built = BuildManagerWithTopology(Substitute.For<IUxMetricsService>(), topology, excludes: excludes);
        using var manager = built.Manager;

        const string topic = "spBv1.0/g/NBIRTH/n1";
        var fireTask = built.Fire(MakeBinaryArgs(topic, SparkplugPayload()));
        var reloaded = false;
        var publishedWhileParked = false;
        try
        {
            (await SatisfiesAsync(() => topology.Reached, TimeSpan.FromSeconds(5))).Should().BeTrue(
                "the message has to be parked inside the read gate before the reload installs filters");
            session.SelectedConnection = new Connection { Id = Guid.NewGuid(), TopicExcludes = ["spBv1.0/#"] };
            reloaded = await SatisfiesAsync(() => excludes.IsExcluded(topic), TimeSpan.FromSeconds(2));
            publishedWhileParked = published.Count > 0;
        }
        finally
        {
            topology.Release();
        }

        await fireTask.WaitAsync(TimeSpan.FromSeconds(10));

        reloaded.Should().BeTrue();
        publishedWhileParked.Should().BeFalse(
            "a reload must not purge while a message admitted under the old filters is still writing");
        published.Should().Equal("spBv1.0/#");
        manager.RootTopicCount.Should().Be(0);
        topology.Groups.Should().BeEmpty();
    }

    [Test]
    public async Task MessageReceived_SubscriberAddsExclusion_CompletesWithoutDeadlock()
    {
        var client = Substitute.For<IMqttManagedClient>();
        Func<MqttApplicationMessageReceivedEventArgs, Task>? handler = null;
        client.When(x => x.ApplicationMessageReceivedAsync += Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>())
            .Do(x => handler = x.Arg<Func<MqttApplicationMessageReceivedEventArgs, Task>>());

        var session = new SessionState { SelectedConnection = new Connection() };
        using var excludes = new TopicExcludeService(
            Substitute.For<ILogger<TopicExcludeService>>(),
            Substitute.For<IConnectionSettings>(), session);
        var performance = Substitute.For<IPerformanceSettings>();
        performance.Performance.Returns(new AppConfiguration().Performance);
        var sparkplug = Substitute.For<ISparkplugSettings>();
        sparkplug.Sparkplug.Returns(new SparkplugSettings { EnrichAliasNames = true });

        using var manager = new MessageStoreManager(client, Substitute.For<ILogger<MessageStoreManager>>(),
            performance, Substitute.For<IUxMetricsService>(), TestPipelineHelper.BuildBuiltInPipeline(),
            sparkplug, topicExcludeService: excludes);
        await manager.Start();
        manager.MessageReceived += async _ => await excludes.Add("sensors/#");

        await handler!(MakeArgs("sensors/temp", "hello")).WaitAsync(TimeSpan.FromSeconds(10));

        excludes.IsExcluded("sensors/temp").Should().BeTrue();
        manager.RootTopicCount.Should().Be(0);
    }

    [Test]
    public async Task TopicExclusion_UnrelatedInFlightMessage_DoesNotStallTheWaitingAdd()
    {
        var session = new SessionState { SelectedConnection = new Connection() };
        using var excludes = new TopicExcludeService(
            Substitute.For<ILogger<TopicExcludeService>>(),
            Substitute.For<IConnectionSettings>(), session);
        var topology = new BarrierTopologyService();

        var parkRequested = false;
        var parkReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parkRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var metrics = Substitute.For<IUxMetricsService>();
        metrics.When(m => m.RecordPayloadSize(Arg.Any<long>())).Do(_ =>
        {
            if (!Volatile.Read(ref parkRequested))
                return;

            Volatile.Write(ref parkRequested, false);
            parkReached.TrySetResult();
            parkRelease.Task.Wait(TimeSpan.FromSeconds(30));
        });

        var built = BuildManagerWithTopology(metrics, topology, excludes: excludes);
        using var manager = built.Manager;

        const string parkedTopic = "spBv1.0/g/NBIRTH/n1";
        var fireMatching = built.Fire(MakeBinaryArgs(parkedTopic, SparkplugPayload()));
        Task<TopicExcludeOperationResult>? addTask = null;
        Task? fireUnrelated = null;
        var filterApplied = false;
        var unrelatedStillInFlight = false;
        try
        {
            (await SatisfiesAsync(() => topology.Reached, TimeSpan.FromSeconds(5))).Should().BeTrue(
                "the matching message has to be parked inside the read gate before Add starts");
            addTask = excludes.Add("spBv1.0/#");
            filterApplied = await SatisfiesAsync(
                () => excludes.IsExcluded(parkedTopic), TimeSpan.FromSeconds(2));

            // A second message, on a topic the new filter cannot match, stays in flight while
            // the purge is waiting.
            Volatile.Write(ref parkRequested, true);
            fireUnrelated = Task.Run(() => built.Fire(MakeArgs("other/topic", "payload")));
            (await SatisfiesAsync(() => parkReached.Task.IsCompleted, TimeSpan.FromSeconds(5)))
                .Should().BeTrue();

            topology.Release();

            var result = await addTask.WaitAsync(TimeSpan.FromSeconds(10));
            await fireMatching.WaitAsync(TimeSpan.FromSeconds(10));
            unrelatedStillInFlight = !fireUnrelated.IsCompleted;

            result.IsValid.Should().BeTrue(
                "the purge may not wait on traffic the new filter cannot match");
            filterApplied.Should().BeTrue();
        }
        finally
        {
            topology.Release();
            parkRelease.TrySetResult();
        }

        unrelatedStillInFlight.Should().BeTrue(
            "the unrelated message was still in flight when the purge published");
        await fireUnrelated.WaitAsync(TimeSpan.FromSeconds(10));

        var roots = manager.GetTopicTreeSnapshot().Roots.Select(node => node.FullTopic);
        roots.Should().Contain("other");
        roots.Should().NotContain("spBv1.0");
    }

    // Real topology service plus a started manager, seeded with one node so a purge has
    // something to remove.
    private static async Task<(TopicExcludeService Excludes, SparkplugTopologyService Topology,
        MessageStoreManager Manager)> BuildExclusionScenarioAsync()
    {
        var session = new SessionState { SelectedConnection = new Connection() };
        var settings = Substitute.For<IConnectionSettings>();
        settings.Connections.Returns([]);
        var excludes = new TopicExcludeService(
            Substitute.For<ILogger<TopicExcludeService>>(), settings, session);
        var topology = new SparkplugTopologyService();
        var built = BuildManagerWithTopology(Substitute.For<IUxMetricsService>(), topology, excludes: excludes);
        await built.Fire(MakeBinaryArgs("spBv1.0/g/NBIRTH/n1", SparkplugPayload()));
        return (excludes, topology, built.Manager);
    }

    private sealed class ReentrantTopologySubscriber
    {
        private readonly TopicExcludeService _excludes;
        private int _notified;
        private int _reentered;
        private int _innerCompleted;

        public ReentrantTopologySubscriber(ISparkplugTopologyService topology, TopicExcludeService excludes)
        {
            _excludes = excludes;
            topology.TopologyChanged += OnTopologyChanged;
        }

        public int Notified => Volatile.Read(ref _notified);

        public int Reentered => Volatile.Read(ref _reentered);

        public bool InnerCompleted => Volatile.Read(ref _innerCompleted) != 0;

        private void OnTopologyChanged()
        {
            Interlocked.Increment(ref _notified);
            if (Interlocked.Exchange(ref _reentered, 1) != 0)
                return;

            try
            {
                // Bounded: a re-entrant deadlock has to surface as a failed assertion, not a
                // hung run whose cleanup then blocks on the lock the deadlock is holding.
                _excludes.Add("reentrant/#").WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                Interlocked.Exchange(ref _innerCompleted, 1);
            }
            catch (Exception)
            {
                // Surfaced through InnerCompleted; nothing may escape into the purge.
            }
        }
    }

    [Test]
    public async Task TopologyChanged_SubscriberReenteringAddDuringImmediatePurge_CompletesWithoutDeadlock()
    {
        var scenario = await BuildExclusionScenarioAsync();
        using var excludes = scenario.Excludes;
        using var manager = scenario.Manager;
        var topology = scenario.Topology;
        var subscriber = new ReentrantTopologySubscriber(topology, excludes);

        // Pooled so a re-entrant deadlock shows up as a bounded failure, not a hung run.
        var result = await Task.Run(() => excludes.Add("spBv1.0/#").WaitAsync(TimeSpan.FromSeconds(10)))
            .WaitAsync(TimeSpan.FromSeconds(15));

        result.IsValid.Should().BeTrue();
        subscriber.Reentered.Should().Be(1, "the subscriber has to re-enter the exclude service for this to prove anything");
        subscriber.InnerCompleted.Should().BeTrue("the re-entrant Add must finish, not just start");
        subscriber.Notified.Should().BeGreaterThan(0, "the purge must still announce the topology it removed");
        topology.Groups.Should().BeEmpty();
        excludes.IsExcluded("reentrant/#").Should().BeTrue();
        manager.RootTopicCount.Should().Be(0);
    }

    [Test]
    public async Task TopologyChanged_SubscriberReenteringAddDuringDeferredPurge_CompletesWithoutDeadlock()
    {
        var scenario = await BuildExclusionScenarioAsync();
        using var excludes = scenario.Excludes;
        using var manager = scenario.Manager;
        var topology = scenario.Topology;
        var subscriber = new ReentrantTopologySubscriber(topology, excludes);
        const string topic = "spBv1.0/g/NBIRTH/n1";

        var gate = excludes.TryEnter(topic);
        gate.Should().NotBeNull();
        var addTask = excludes.Add("spBv1.0/#");
        (await SatisfiesAsync(() => excludes.IsExcluded(topic), TimeSpan.FromSeconds(2))).Should().BeTrue(
            "the filter has to be live before the purge parks");
        addTask.IsCompleted.Should().BeFalse("the purge is parked on the in-flight reader");

        // Releasing on the pool keeps a deadlock visible as a bounded failure, not a hang.
        await Task.Run(() => gate.Dispose()).WaitAsync(TimeSpan.FromSeconds(15));
        var result = await addTask.WaitAsync(TimeSpan.FromSeconds(10));

        result.IsValid.Should().BeTrue();
        subscriber.Reentered.Should().Be(1);
        subscriber.InnerCompleted.Should().BeTrue("the re-entrant Add must finish, not just start");
        subscriber.Notified.Should().BeGreaterThan(0);
        topology.Groups.Should().BeEmpty();
        manager.RootTopicCount.Should().Be(0);
    }

    [Test]
    public async Task TopologyChanged_SubscriberThrowing_DoesNotFailTheExclusion()
    {
        var scenario = await BuildExclusionScenarioAsync();
        using var excludes = scenario.Excludes;
        using var manager = scenario.Manager;
        scenario.Topology.TopologyChanged += () => throw new InvalidOperationException("subscriber boom");

        var result = await Task.Run(() => excludes.Add("spBv1.0/#").WaitAsync(TimeSpan.FromSeconds(10)))
            .WaitAsync(TimeSpan.FromSeconds(15));

        result.IsValid.Should().BeTrue("a throwing topology subscriber is isolated from the purge outcome");
        scenario.Topology.Groups.Should().BeEmpty();
    }

    [Test]
    public async Task Dispose_BetweenPurgeAndObserver_DropsThePendingTopologyNotification()
    {
        var scenario = await BuildExclusionScenarioAsync();
        using var manager = scenario.Manager;
        using var excludes = scenario.Excludes;
        var topology = scenario.Topology;
        var notified = 0;
        topology.TopologyChanged += () => Interlocked.Increment(ref notified);

        // Registered after the manager's purger, so this one runs inside the trusted purge
        // and holds it open while the manager tears down in between.
        var purgerReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePurger = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        excludes.PurgeExcludedTopic += _ =>
        {
            purgerReached.TrySetResult();
            releasePurger.Task.Wait(TimeSpan.FromSeconds(30));
        };

        var addTask = Task.Run(() => excludes.Add("spBv1.0/#"));
        try
        {
            (await SatisfiesAsync(() => purgerReached.Task.IsCompleted, TimeSpan.FromSeconds(5))).Should().BeTrue(
                "the purge has to be mid-flight before the observer is torn down");

            await Task.Run(() => manager.Dispose()).WaitAsync(TimeSpan.FromSeconds(10));

            notified.Should().Be(0,
                "teardown may not announce topology while a purge still holds the exclude service");
        }
        finally
        {
            releasePurger.TrySetResult();
        }

        var result = await addTask.WaitAsync(TimeSpan.FromSeconds(10));

        result.IsValid.Should().BeTrue();
        notified.Should().Be(0, "the detached observer must not announce it either");
        topology.Groups.Should().BeEmpty();
    }

    [Test]
    public async Task Dispose_WhileTrustedPurgeActive_SubscriberReenteringAdd_DoesNotDeadlock()
    {
        var scenario = await BuildExclusionScenarioAsync();
        using var manager = scenario.Manager;
        using var excludes = scenario.Excludes;
        var topology = scenario.Topology;
        var subscriber = new ReentrantTopologySubscriber(topology, excludes);

        var purgerReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePurger = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        excludes.PurgeExcludedTopic += _ =>
        {
            purgerReached.TrySetResult();
            releasePurger.Task.Wait(TimeSpan.FromSeconds(30));
        };

        var addTask = Task.Run(() => excludes.Add("spBv1.0/#"));
        try
        {
            (await SatisfiesAsync(() => purgerReached.Task.IsCompleted, TimeSpan.FromSeconds(5))).Should().BeTrue(
                "the purge has to be mid-flight before teardown");

            // Bounded: a teardown that raises into a subscriber re-entering the exclude
            // service here blocks on the operation lock the purge still owns, and has to
            // surface as a timeout instead of a hung run.
            await Task.Run(() => manager.Dispose()).WaitAsync(TimeSpan.FromSeconds(10));

            subscriber.Notified.Should().Be(0,
                "teardown must not raise TopologyChanged while the purge holds the exclude service");
        }
        finally
        {
            releasePurger.TrySetResult();
        }

        var result = await addTask.WaitAsync(TimeSpan.FromSeconds(10));

        result.IsValid.Should().BeTrue();
        subscriber.Reentered.Should().Be(0, "without a teardown raise there is no re-entrant Add to deadlock against");
        topology.Groups.Should().BeEmpty();
        manager.RootTopicCount.Should().Be(0);
    }

    private static async Task<bool> SatisfiesAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;

            await Task.Delay(10);
        }

        return condition();
    }

    private sealed class BarrierTopologyService : ISparkplugTopologyService
    {
        private readonly SparkplugTopologyService _inner = new();
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Reached => _reached.Task.IsCompleted;

        public void Release() => _release.TrySetResult();

        public IReadOnlyDictionary<string, SpbGroup> Groups => _inner.Groups;

        public event Action? TopologyChanged
        {
            add => _inner.TopologyChanged += value;
            remove => _inner.TopologyChanged -= value;
        }

        public bool RemoveNode(string groupId, string nodeId) => _inner.RemoveNode(groupId, nodeId);

        public int RemoveMatchingTopic(string filter) => _inner.RemoveMatchingTopic(filter);

        public int RemoveMatchingTopicSilent(string filter) => _inner.RemoveMatchingTopicSilent(filter);

        public void RaiseTopologyChanged() => _inner.RaiseTopologyChanged();

        public int RemoveOfflineNodes() => _inner.RemoveOfflineNodes();

        public void ClearAll() => _inner.ClearAll();

        public async Task ApplyTopologyEventsAsync(IReadOnlyList<TopologyEvent> events)
        {
            _reached.TrySetResult();
            await _release.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await _inner.ApplyTopologyEventsAsync(events);
        }
    }
}
