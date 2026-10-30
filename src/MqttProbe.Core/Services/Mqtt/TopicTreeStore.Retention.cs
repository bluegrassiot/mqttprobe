using MqttProbe.Core.Models.Mqtt;

namespace MqttProbe.Core.Services.Mqtt;

internal sealed partial class TopicTreeStore
{
    private void RebuildRetentionOrder()
    {
        _retentionOrder.Clear();
        foreach (var item in EnumerateMessages(_messageStores.Values)
                     .OrderBy(item => item.Message.DateTimeReceived))
            _retentionOrder.Enqueue(new RetentionEntry(item.Store, item.Message));
    }

    private readonly record struct RetentionEntry(MessageStore Store, MqttMessage Message);
}
