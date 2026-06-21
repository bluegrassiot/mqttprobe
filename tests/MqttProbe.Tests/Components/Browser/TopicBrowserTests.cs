using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Models.Mqtt;
using MqttProbe.Services.Mqtt;
using MqttProbe.Shared.Tests.TestHelpers;

namespace MqttProbe.Shared.Tests.Components.Browser;

[TestFixture]
public class TopicBrowserTests : BunitTestContext
{
    private IMessageStoreManager _mockMsgStore = null!;

    [SetUp]
    public void SetupMocks()
    {
        _mockMsgStore = Substitute.For<IMessageStoreManager>();
        _mockMsgStore.MessageStores.Returns(
            new ConcurrentDictionary<string, MessageStore>());
        Services.AddSingleton(_mockMsgStore);
    }

    [Test]
    public void Renders_NoMessagesText_WhenMessageStoresIsEmpty()
    {
        var cut = Render<TopicBrowser>();

        cut.Markup.Should().Contain("No messages received");
    }

    [Test]
    public void Renders_TreeItems_WhenMessageStoresHasEntries()
    {
        var stores = new ConcurrentDictionary<string, MessageStore>();
        stores["sensor"] = new MessageStore
        {
            Topic = "sensor",
            Messages = new ConcurrentQueue<MqttMessage>()
        };
        _mockMsgStore.MessageStores.Returns(stores);

        var cut = Render<TopicBrowser>();

        cut.Markup.Should().NotContain("No messages received");
        cut.Markup.Should().Contain("sensor");
    }

    [Test]
    public async Task DisposeAsync_DoesNotThrow()
    {
        var cut = Render<TopicBrowser>();

        var act = async () => await cut.Instance.DisposeAsync();

        await act.Should().NotThrowAsync();
    }

    [Test]
    public async Task DisposeAsync_AfterTimerHasFired_DoesNotThrow()
    {
        var stores = new ConcurrentDictionary<string, MessageStore>();
        stores["sensor"] = new MessageStore
        {
            Topic = "sensor",
            Messages = new ConcurrentQueue<MqttMessage>()
        };
        _mockMsgStore.MessageStores.Returns(stores);

        var cut = Render<TopicBrowser>();
        await Task.Delay(50); // let the immediate timer tick dispatch a render

        var act = async () => await cut.Instance.DisposeAsync();

        await act.Should().NotThrowAsync();
    }
}

[TestFixture]
public class RecursiveTreeTests : BunitTestContext
{
    [Test]
    public void LeafNode_ShowsMessageCount()
    {
        var messages = new ConcurrentQueue<MqttMessage>();
        messages.Enqueue(new MqttMessage { Topic = "sensor/temp", Payload = "42" });
        messages.Enqueue(new MqttMessage { Topic = "sensor/temp", Payload = "43" });

        var store = new MessageStore
        {
            Topic = "temp",
            SubTopics = new ConcurrentDictionary<string, MessageStore>(),
            Messages = messages
        };

        var cut = Render<RecursiveTree>(p => p.Add(r => r.MessageStore, store));

        cut.Markup.Should().Contain("2");
    }

    [Test]
    public void InternalNode_RendersChildTopics()
    {
        var subtopics = new ConcurrentDictionary<string, MessageStore>();
        subtopics["temp"] = new MessageStore { Topic = "temp" };
        subtopics["humidity"] = new MessageStore { Topic = "humidity" };

        var store = new MessageStore
        {
            Topic = "sensor",
            SubTopics = subtopics
        };

        var cut = Render<RecursiveTree>(p => p.Add(r => r.MessageStore, store));

        cut.Markup.Should().Contain("sensor");
        cut.Markup.Should().Contain("temp");
        cut.Markup.Should().Contain("humidity");
    }

    [Test]
    public void ReusedComponent_WithDifferentStoreSameChildCount_RendersNewChildren()
    {
        var first = new MessageStore
        {
            Topic = "benchmarks",
            FullTopic = "benchmarks",
            SubTopics = new ConcurrentDictionary<string, MessageStore>
            {
                ["payloads"] = new() { Topic = "payloads", FullTopic = "benchmarks/payloads" }
            }
        };
        var second = new MessageStore
        {
            Topic = "spBv1.0",
            FullTopic = "spBv1.0",
            SubTopics = new ConcurrentDictionary<string, MessageStore>
            {
                ["bench"] = new() { Topic = "bench", FullTopic = "spBv1.0/bench" }
            }
        };

        var cut = Render<RecursiveTreeHost>();
        cut.Instance.Store = first;
        cut.Render();

        cut.Instance.Store = second;
        cut.Render();

        cut.Markup.Should().Contain("spBv1.0");
        cut.Markup.Should().Contain("bench");
        cut.Markup.Should().NotContain("payloads");
    }
}
