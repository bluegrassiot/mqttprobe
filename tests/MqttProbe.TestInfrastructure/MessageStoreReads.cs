using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Mqtt;

namespace MqttProbe.TestInfrastructure;

public static class MessageStoreReads
{
    public static async Task<IReadOnlyList<MqttMessage>> ReadMessagesAsync(
        IMessageStoreManager manager,
        string topic,
        int limit)
    {
        manager.SelectTopic(topic);

        var selection = manager.GetSelectedTopicState();
        // Missing or invalid topics leave the previous selection active.
        var normalizedTopic = string.Join('/', topic.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries));
        if (!string.Equals(selection.FullTopic, normalizedTopic, StringComparison.Ordinal))
            return [];

        var snapshot = await manager.GetSelectedMessagesAsync(selection.Token, limit);
        return snapshot?.Messages ?? throw new InvalidOperationException(
            "The selected message snapshot is stale or unavailable.");
    }
}
