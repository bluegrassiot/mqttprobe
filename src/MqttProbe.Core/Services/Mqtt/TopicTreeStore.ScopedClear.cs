using System.Collections.Concurrent;
using MqttProbe.Core.Models.Mqtt;

namespace MqttProbe.Core.Services.Mqtt;

internal sealed partial class TopicTreeStore
{
    public int ClearMessagesForTopics(IReadOnlyList<string> fullTopics)
    {
        lock (_storeSync)
        {
            var targets = new Dictionary<MessageStore, HashSet<string>>(ReferenceEqualityComparer.Instance);
            foreach (var topic in fullTopics)
            {
                var normalized = Normalize(topic);
                if (normalized is null || FindNode(normalized) is not { } node)
                    continue;

                if (!targets.TryGetValue(node, out var spellings))
                    targets[node] = spellings = new HashSet<string>(StringComparer.Ordinal);
                spellings.Add(topic);
            }

            var removedByStore = new Dictionary<MessageStore, HashSet<MqttMessage>>(ReferenceEqualityComparer.Instance);
            var removed = 0;
            foreach (var (node, spellings) in targets)
            {
                if (node.Messages is not { IsEmpty: false } messages)
                    continue;

                var kept = new ConcurrentQueue<MqttMessage>();
                var nodeRemoved = 0;
                var removedHere = new HashSet<MqttMessage>(ReferenceEqualityComparer.Instance);
                foreach (var message in messages)
                {
                    if (spellings.Contains(message.Topic ?? node.FullTopic ?? string.Empty))
                    {
                        removedHere.Add(message);
                        nodeRemoved++;
                    }
                    else
                        kept.Enqueue(message);
                }

                if (nodeRemoved == 0)
                    continue;

                node.Messages = kept.IsEmpty ? null : kept;
                for (var current = node; current is not null; current = current.Parent)
                    current.MessageCount -= nodeRemoved;

                removedByStore[node] = removedHere;
                removed += nodeRemoved;
            }

            if (removed == 0)
                return 0;

            _totalMessageCount -= removed;
            RemoveRetentionEntries(removedByStore);
            if (_selectedRoots.Count > 0)
                _selectionContentVersion++;
            Interlocked.Increment(ref _globalVersion);
            InvalidateTopicTree();
            return removed;
        }
    }

    private void RemoveRetentionEntries(Dictionary<MessageStore, HashSet<MqttMessage>> removedByStore)
    {
        var retained = _retentionOrder
            .Where(entry => !removedByStore.TryGetValue(entry.Store, out var messages)
                            || !messages.Contains(entry.Message))
            .ToList();

        _retentionOrder.Clear();
        foreach (var entry in retained)
            _retentionOrder.Enqueue(entry);
    }
}
