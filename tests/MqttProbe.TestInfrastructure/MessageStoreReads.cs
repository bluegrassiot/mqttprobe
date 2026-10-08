using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Mqtt;

namespace MqttProbe.TestInfrastructure;

// Reads stored messages for an arbitrary topic without disturbing the selection a test
// is asserting on. SelectTopic is a no-op for an unknown topic, so an unmatched result
// means the caller asked for a topic that does not exist and gets an empty read, which
// is what the store returned before the read seam was introduced.
public static class MessageStoreReads
{
    public static async Task<IReadOnlyList<MqttMessage>> ReadMessagesAsync(
        IMessageStoreManager manager,
        string topic,
        int limit)
    {
        manager.SelectTopic(topic);

        var selection = manager.GetSelectedTopicState();
        if (!string.Equals(selection.FullTopic, topic, StringComparison.Ordinal))
            return [];

        var snapshot = await manager.GetSelectedMessagesAsync(selection.Token, limit);
        return snapshot?.Messages ?? [];
    }
}
