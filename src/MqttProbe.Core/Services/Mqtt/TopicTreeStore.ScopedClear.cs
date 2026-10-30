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
                if (FindNode(topic) is not { } node)
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
            if (HasSelection())
                _selectionContentVersion++;
            Interlocked.Increment(ref _globalVersion);
            InvalidateTopicTree();
            return removed;
        }
    }

    public int ClearMessagesForBranches(IReadOnlyList<TopicBranchTarget> branches)
    {
        if (branches is null || branches.Count == 0)
            return 0;

        lock (_storeSync)
        {
            var targets = branches.Where(branch => branch is not null).ToArray();
            if (targets.Length == 0)
                return 0;

            var removedByStore = new Dictionary<MessageStore, HashSet<MqttMessage>>(ReferenceEqualityComparer.Instance);
            var removed = 0;
            var changed = false;

            foreach (var (key, root) in _messageStores.ToArray())
            {
                if (!ClearBranchNode(root, targets, removedByStore, ref removed, out var rootRemoved))
                    continue;

                changed = true;
                if (rootRemoved && _messageStores.TryRemove(key, out _))
                    _rootTopicCount--;
            }

            if (!changed)
                return 0;

            RemoveRetentionEntries(removedByStore);
            RecalculateCounters();
            if (HasSelection())
                _selectionContentVersion++;
            Interlocked.Increment(ref _globalVersion);
            InvalidateTopicTree();
            return removed;
        }
    }

    private bool ClearBranchNode(MessageStore node, TopicBranchTarget[] targets,
        Dictionary<MessageStore, HashSet<MqttMessage>> removedByStore, ref int removed, out bool nodeRemoved)
    {
        var changed = false;

        if (node.SubTopics is not null)
        {
            foreach (var (key, child) in node.SubTopics.ToArray())
            {
                if (!ClearBranchNode(child, targets, removedByStore, ref removed, out var childRemoved))
                    continue;

                changed = true;
                if (childRemoved || IsEmpty(child))
                    node.SubTopics.TryRemove(key, out _);
            }

            if (node.SubTopics.IsEmpty)
                node.SubTopics = null;
        }

        if (node.Messages is { IsEmpty: false } messages)
        {
            var kept = new ConcurrentQueue<MqttMessage>();
            var removedHere = new HashSet<MqttMessage>(ReferenceEqualityComparer.Instance);
            foreach (var message in messages)
            {
                if (MatchesAnyBranch(targets, message.Topic ?? node.FullTopic ?? string.Empty))
                {
                    removedHere.Add(message);
                    removed++;
                }
                else
                    kept.Enqueue(message);
            }

            if (removedHere.Count > 0)
            {
                node.Messages = kept.IsEmpty ? null : kept;
                removedByStore[node] = removedHere;
                changed = true;
            }
        }

        if (RetireBranchObservedTopics(node, targets))
            changed = true;

        // A node-budget partial insert can leave an empty structural node with no history;
        // a covered one is prunable so a clear removes the whole branch.
        var covered = MatchesAnyBranch(targets, node.FullTopic ?? string.Empty);
        nodeRemoved = IsEmpty(node) && (changed || covered);
        return changed || nodeRemoved;
    }

    private static bool MatchesAnyBranch(TopicBranchTarget[] targets, string topic) =>
        targets.Any(target => TopicBranchMatcher.Matches(target, topic));

    // History retires by the raw names it was observed under, so a covered spelling is dropped
    // even when no stored message remains.
    private bool RetireBranchObservedTopics(MessageStore node, TopicBranchTarget[] targets)
    {
        var observed = node.ObservedRawTopics;
        if (observed is null)
            return false;

        var retired = 0;
        foreach (var raw in observed.ToArray())
        {
            if (!MatchesAnyBranch(targets, raw))
                continue;

            observed.Remove(raw);
            retired++;
            if (!_aliasSpellings.Remove(raw))
                continue;

            _aliasCharacterCount -= raw.Length;
        }

        if (retired == 0)
            return false;

        if (observed.Count == 0)
            node.ObservedRawTopics = null;

        return true;
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
