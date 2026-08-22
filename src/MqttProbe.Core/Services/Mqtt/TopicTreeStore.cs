using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Configuration;

namespace MqttProbe.Core.Services.Mqtt;

internal sealed class TopicTreeStore(IPerformanceSettings performanceSettings, ILogger logger)
{
    private readonly Lock _storeSync = new();
    private readonly Queue<MessageStore> _retentionOrder = new();

    private int _totalNodeCount;
    private int _totalMessageCount;
    private bool _nodeLimitLogged;
    private long _globalVersion;
    private long _selectedTopicVersion;

    public ConcurrentDictionary<string, MessageStore> MessageStores { get; } = new(StringComparer.Ordinal);

    // Written lock-free from the UI; reads inside _storeSync see whatever was last assigned.
    public MessageStore? SelectedMessageStore { get; set; }

    public int MaxStoredMessages => performanceSettings.Performance.MaxStoredMessages;

    public int MaxTopicNodes => performanceSettings.Performance.MaxTopicNodes;

    public int TotalStoredMessages
    {
        get { lock (_storeSync) { return _totalMessageCount; } }
    }

    public int TopicNodeCount => Volatile.Read(ref _totalNodeCount);

    public long Version => Interlocked.Read(ref _globalVersion);

    public long SelectedTopicVersion => Interlocked.Read(ref _selectedTopicVersion);

    public void Add(string fullTopic, MqttMessage message)
    {
        lock (_storeSync)
        {
            var normalized = Normalize(fullTopic);
            if (normalized is null) return;

            var firstSlash = normalized.IndexOf('/', StringComparison.Ordinal);
            var rootKey = firstSlash >= 0 ? normalized[..firstSlash] : normalized;

            if (!MessageStores.TryGetValue(rootKey, out var messageStore))
            {
                if (_totalNodeCount >= MaxTopicNodes)
                {
                    LogNodeLimit();
                    return;
                }

                var candidate = new MessageStore { Topic = rootKey, FullTopic = rootKey };
                messageStore = MessageStores.GetOrAdd(rootKey, candidate);
                if (ReferenceEquals(messageStore, candidate))
                    Interlocked.Increment(ref _totalNodeCount);
            }

            if (firstSlash >= 0)
                AddData(normalized, firstSlash + 1, messageStore, message);
            else
                StoreMessage(messageStore, message);
        }
    }

    public void Clear()
    {
        lock (_storeSync)
        {
            MessageStores.Clear();
            SelectedMessageStore = null;
            _totalNodeCount = 0;
            _totalMessageCount = 0;
            _retentionOrder.Clear();
            _nodeLimitLogged = false;
            Interlocked.Increment(ref _globalVersion);
            Interlocked.Increment(ref _selectedTopicVersion);
        }
    }

    public void ApplyRetentionLimit()
    {
        lock (_storeSync) { TrimToLimit(); }
    }

    public void RemoveMatchingTopic(string filter)
    {
        lock (_storeSync)
        {
            var removed = false;
            foreach (var (key, store) in MessageStores.ToArray())
            {
                if (!PurgeNode(store, filter, out var nodeRemoved))
                    continue;

                removed = true;
                if (nodeRemoved)
                    MessageStores.TryRemove(key, out _);
            }

            if (!removed)
                return;

            RecalculateCounters();
            RebuildRetentionOrder();
            Interlocked.Increment(ref _globalVersion);
            Interlocked.Increment(ref _selectedTopicVersion);
        }
    }

    public IEnumerable<MqttMessage> GetMessagesForSelectedTopic()
    {
        lock (_storeSync)
        {
            if (SelectedMessageStore == null)
                return [];

            var list = new List<MqttMessage>();
            CollectMessages(SelectedMessageStore, list);
            list.Sort(static (a, b) => b.DateTimeReceived.CompareTo(a.DateTimeReceived));
            return list;
        }
    }

    public IReadOnlyList<MqttMessage> GetRecentMessages(string topic, int limit)
    {
        lock (_storeSync)
        {
            var node = FindNode(topic);
            if (node is null)
                return [];

            var collected = new List<MqttMessage>();
            CollectMessages(node, collected);
            if (collected.Count == 0)
                return [];

            collected.Sort(static (a, b) => b.DateTimeReceived.CompareTo(a.DateTimeReceived));

            return collected.Count <= limit ? collected : collected.GetRange(0, limit);
        }
    }

    internal static string? Normalize(string fullTopic)
    {
        var trimmed = fullTopic.Trim('/');
        if (trimmed.Length == 0) return null;

        var segments = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('/', segments);
    }

    private MessageStore? FindNode(string fullTopic)
    {
        var segments = fullTopic.Split('/');
        if (segments.Length == 0) return null;

        if (!MessageStores.TryGetValue(segments[0], out var current))
            return null;

        for (var i = 1; i < segments.Length; i++)
        {
            if (current.SubTopics is null || !current.SubTopics.TryGetValue(segments[i], out current))
                return null;
        }

        return current;
    }

    private void AddData(string fullTopic, int startIndex, MessageStore parent, MqttMessage message)
    {
        var nextSlash = fullTopic.IndexOf('/', startIndex);
        var hasChildren = nextSlash >= 0;
        var segEnd = hasChildren ? nextSlash : fullTopic.Length;
        var levelKey = fullTopic.Substring(startIndex, segEnd - startIndex);

        MessageStore? child = null;
        parent.SubTopics?.TryGetValue(levelKey, out child);
        if (child == null)
        {
            if (_totalNodeCount >= MaxTopicNodes)
            {
                LogNodeLimit();
                return;
            }

            var fullPath = fullTopic[..segEnd];
            var candidate = new MessageStore { Topic = levelKey, FullTopic = fullPath, Parent = parent };
            var subTopics = parent.SubTopics ??= new ConcurrentDictionary<string, MessageStore>(StringComparer.Ordinal);
            child = subTopics.GetOrAdd(levelKey, candidate);
            if (ReferenceEquals(child, candidate))
            {
                Interlocked.Increment(ref _totalNodeCount);
                IncrementTopicCounts(parent);
            }
        }

        if (hasChildren)
            AddData(fullTopic, nextSlash + 1, child, message);
        else
            StoreMessage(child, message);
    }

    private void StoreMessage(MessageStore store, MqttMessage message)
    {
        store.Messages ??= new ConcurrentQueue<MqttMessage>();
        store.Messages.Enqueue(message);
        _retentionOrder.Enqueue(store);
        _totalMessageCount++;
        IncrementMessageCounts(store);
        Interlocked.Increment(ref _globalVersion);
        if (IsSelectedTopicOrDescendant(store))
            Interlocked.Increment(ref _selectedTopicVersion);
        TrimToLimit();
    }

    private void TrimToLimit()
    {
        var limit = MaxStoredMessages;
        while (_totalMessageCount > limit && _retentionOrder.Count > 0)
        {
            var oldest = _retentionOrder.Dequeue();
            oldest.Messages?.TryDequeue(out _);
            _totalMessageCount--;
            DecrementMessageCounts(oldest);
            Interlocked.Increment(ref _globalVersion);
            if (IsSelectedTopicOrDescendant(oldest))
                Interlocked.Increment(ref _selectedTopicVersion);
        }
    }

    private static bool PurgeNode(MessageStore node, string filter, out bool nodeRemoved)
    {
        var changed = TopicExcludeService.Matches(node.FullTopic ?? string.Empty, filter);
        if (node.SubTopics is not null)
        {
            foreach (var (key, child) in node.SubTopics.ToArray())
            {
                if (!PurgeNode(child, filter, out var childRemoved))
                    continue;

                changed = true;
                if (childRemoved || IsEmpty(child))
                    node.SubTopics.TryRemove(key, out _);
            }

            if (node.SubTopics.IsEmpty)
                node.SubTopics = null;
        }

        if (node.Messages is { IsEmpty: false })
        {
            var remaining = new ConcurrentQueue<MqttMessage>();
            while (node.Messages.TryDequeue(out var message))
            {
                if (TopicExcludeService.Matches(message.Topic ?? node.FullTopic ?? string.Empty, filter))
                    changed = true;
                else
                    remaining.Enqueue(message);
            }

            node.Messages = remaining.IsEmpty ? null : remaining;
        }

        nodeRemoved = changed && IsEmpty(node);

        return changed;
    }

    private static bool IsEmpty(MessageStore store) =>
        (store.Messages is null || store.Messages.IsEmpty)
        && (store.SubTopics is null || store.SubTopics.IsEmpty);

    private void RecalculateCounters()
    {
        _totalNodeCount = 0;
        _totalMessageCount = 0;
        foreach (var store in MessageStores.Values)
            RecalculateNode(store);

        if (SelectedMessageStore is not null && !ContainsStore(SelectedMessageStore))
            SelectedMessageStore = null;
    }

    private void RecalculateNode(MessageStore node)
    {
        _totalNodeCount++;
        node.MessageCount = node.Messages?.Count ?? 0;
        node.TopicCount = 0;
        _totalMessageCount += node.MessageCount;

        if (node.SubTopics is null)
            return;

        foreach (var child in node.SubTopics.Values)
        {
            child.Parent = node;
            RecalculateNode(child);
            node.TopicCount += child.TopicCount + 1;
            node.MessageCount += child.MessageCount;
        }
    }

    private bool ContainsStore(MessageStore selected)
    {
        return MessageStores.Values.Any(root => ContainsStore(root, selected));
    }

    private static bool ContainsStore(MessageStore current, MessageStore selected)
    {
        if (ReferenceEquals(current, selected))
            return true;

        return current.SubTopics?.Values.Any(child => ContainsStore(child, selected)) == true;
    }

    private void RebuildRetentionOrder()
    {
        _retentionOrder.Clear();
        foreach (var store in EnumerateMessages(MessageStores.Values)
                     .OrderBy(item => item.Message.DateTimeReceived)
                     .Select(item => item.Store))
            _retentionOrder.Enqueue(store);
    }

    private static IEnumerable<(MessageStore Store, MqttMessage Message)> EnumerateMessages(
        IEnumerable<MessageStore> stores)
    {
        foreach (var store in stores)
        {
            if (store.Messages is not null)
            {
                foreach (var message in store.Messages)
                    yield return (store, message);
            }

            if (store.SubTopics is not null)
            {
                foreach (var item in EnumerateMessages(store.SubTopics.Values))
                    yield return item;
            }
        }
    }

    private bool IsSelectedTopicOrDescendant(MessageStore store)
    {
        var selected = SelectedMessageStore;
        if (selected is null) return false;
        if (ReferenceEquals(store, selected)) return true;

        var selectedPath = selected.FullTopic;
        var storePath = store.FullTopic;
        if (selectedPath is null || storePath is null) return false;

        if (storePath.StartsWith(selectedPath, StringComparison.Ordinal)
            && storePath.Length > selectedPath.Length
            && storePath[selectedPath.Length] == '/')
            return true;

        if (selectedPath.StartsWith(storePath, StringComparison.Ordinal)
            && selectedPath.Length > storePath.Length
            && selectedPath[storePath.Length] == '/')
            return true;

        return false;
    }

    private static void CollectMessages(MessageStore? store, List<MqttMessage> result)
    {
        if (store == null) return;

        if (store.Messages is { IsEmpty: false })
            foreach (var msg in store.Messages)
                result.Add(msg);

        if (store.SubTopics == null) return;
        foreach (var sub in store.SubTopics.Values)
            CollectMessages(sub, result);
    }

    private static void IncrementTopicCounts(MessageStore parent)
    {
        for (var node = parent; node != null; node = node.Parent)
            node.TopicCount++;
    }

    private static void IncrementMessageCounts(MessageStore leaf)
    {
        for (var node = leaf; node != null; node = node.Parent)
            node.MessageCount++;
    }

    private static void DecrementMessageCounts(MessageStore leaf)
    {
        for (var node = leaf; node != null; node = node.Parent)
            node.MessageCount--;
    }

    private void LogNodeLimit()
    {
        if (_nodeLimitLogged) return;
        _nodeLimitLogged = true;
        logger.LogWarning("Topic-tree node limit ({Limit}) reached; new topics will be dropped.", MaxTopicNodes);
    }
}
