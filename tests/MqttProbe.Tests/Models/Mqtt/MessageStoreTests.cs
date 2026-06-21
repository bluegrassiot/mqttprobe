using System.Collections.Concurrent;
using MQTTnet.Protocol;
using MqttProbe.Models.Chart;
using MqttProbe.Models.Configuration;
using MqttProbe.Models.Mqtt;
using MqttProbe.Models.Sparkplug;

namespace MqttProbe.Shared.Tests.Models.Mqtt;

[TestFixture]
public class MessageStoreTests
{
    [Test]
    public void SubTopics_IsNull_WhenNew()
    {
        var store = new MessageStore();
        store.SubTopics.Should().BeNull();
    }

    [Test]
    public void Messages_IsNull_WhenNew()
    {
        var store = new MessageStore();
        store.Messages.Should().BeNull();
    }

    [Test]
    public void SubTopics_SupportsAddAndGet()
    {
        var store = new MessageStore
        {
            SubTopics = new ConcurrentDictionary<string, MessageStore>()
        };
        var child = new MessageStore { Topic = "child" };

        store.SubTopics.TryAdd("child", child);

        store.SubTopics.Should().ContainKey("child");
        store.SubTopics["child"].Topic.Should().Be("child");
    }

    [Test]
    public void Messages_SupportsEnqueueAndDequeue()
    {
        var store = new MessageStore
        {
            Messages = new ConcurrentQueue<MqttMessage>()
        };
        var msg = new MqttMessage("payload", "topic", false, MqttQualityOfServiceLevel.AtMostOnce);

        store.Messages.Enqueue(msg);

        store.Messages.TryDequeue(out var dequeued).Should().BeTrue();
        dequeued!.Payload.Should().Be("payload");
    }
}
