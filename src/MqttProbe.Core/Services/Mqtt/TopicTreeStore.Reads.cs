using System.Collections.Immutable;
using MqttProbe.Core.Models.Mqtt;

namespace MqttProbe.Core.Services.Mqtt;

internal sealed partial class TopicTreeStore
{
    private readonly Guid _storeId = Guid.NewGuid();
    private MessageStore? _selectedMessageStore;
    private long _selectionGeneration;
    private long _selectionContentVersion;
    private TopicTreeSnapshot? _cachedTopicTree;

    public int RootTopicCount
    {
        get
        {
            lock (_storeSync)
                return _rootTopicCount;
        }
    }

    public TopicTreeSnapshot GetTopicTreeSnapshot()
    {
        lock (_storeSync)
        {
            if (_cachedTopicTree is not null)
                return _cachedTopicTree;

            var roots = _messageStores.Values
                .Select(CreateNodeSnapshot)
                .ToImmutableArray();
            _cachedTopicTree = new TopicTreeSnapshot(Interlocked.Read(ref _globalVersion), roots);
            return _cachedTopicTree;
        }
    }

    public SelectedTopicState GetSelectedTopicState()
    {
        lock (_storeSync)
            return CaptureSelectedTopicState();
    }

    public void SelectTopic(string? fullTopic)
    {
        lock (_storeSync)
        {
            if (fullTopic is null)
            {
                if (_selectedMessageStore is not null)
                    InvalidateSelection();

                return;
            }

            var normalized = Normalize(fullTopic);
            if (normalized is null)
                return;

            var selected = FindNode(normalized);
            if (selected is null)
                return;

            if (!ReferenceEquals(selected, _selectedMessageStore))
            {
                _selectedMessageStore = selected;
                _selectionGeneration++;
                _selectionContentVersion++;
            }
        }
    }

    public SelectedMessagesSnapshot? GetSelectedMessages(SelectedTopicToken expectedSelection, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);

        SelectedTopicState state;
        MqttMessage[] messages;
        lock (_storeSync)
        {
            if (expectedSelection.StoreId != _storeId
                || expectedSelection.Generation != _selectionGeneration)
                return null;

            state = CaptureSelectedTopicState();
            if (_selectedMessageStore is null || limit == 0)
                messages = [];
            else
            {
                var collected = new List<MqttMessage>();
                CollectMessages(_selectedMessageStore, collected);
                messages = collected.ToArray();
            }
        }

        Array.Sort(messages, static (left, right) => right.DateTimeReceived.CompareTo(left.DateTimeReceived));
        if (messages.Length > limit)
            Array.Resize(ref messages, limit);

        return new SelectedMessagesSnapshot(state, limit, ImmutableArray.CreateRange(messages));
    }

    private SelectedTopicState CaptureSelectedTopicState() => new(
        new SelectedTopicToken(_storeId, _selectionGeneration, _selectionContentVersion),
        _selectedMessageStore?.FullTopic,
        _selectedMessageStore?.MessageCount ?? 0);

    private TopicNodeSnapshot CreateNodeSnapshot(MessageStore node)
    {
        var children = node.SubTopics?.Values
            .Select(CreateNodeSnapshot)
            .ToImmutableArray() ?? ImmutableArray<TopicNodeSnapshot>.Empty;

        return new TopicNodeSnapshot(
            node.Topic ?? string.Empty,
            node.FullTopic ?? string.Empty,
            node.TopicCount,
            node.MessageCount,
            node.Messages is { IsEmpty: false },
            children);
    }

    private void InvalidateTopicTree() => _cachedTopicTree = null;

    private void InvalidateSelection()
    {
        _selectedMessageStore = null;
        _selectionGeneration++;
        _selectionContentVersion++;
    }

    private bool SelectionIncludes(MessageStore store)
    {
        var selected = _selectedMessageStore;
        if (selected is null)
            return false;

        var selectedPath = selected.FullTopic;
        var storePath = store.FullTopic;
        if (selectedPath is null || storePath is null)
            return ReferenceEquals(selected, store);

        return ReferenceEquals(selected, store)
            || IsPathOrDescendant(storePath, selectedPath)
            || IsPathOrDescendant(selectedPath, storePath);
    }

    private static bool IsPathOrDescendant(string path, string ancestor) =>
        path.Equals(ancestor, StringComparison.Ordinal)
        || (path.StartsWith(ancestor, StringComparison.Ordinal)
            && path.Length > ancestor.Length
            && path[ancestor.Length] == '/');
}
