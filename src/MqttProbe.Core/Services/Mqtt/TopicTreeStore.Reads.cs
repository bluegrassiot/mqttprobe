using System.Collections.Immutable;
using MqttProbe.Core.Models.Mqtt;

namespace MqttProbe.Core.Services.Mqtt;

internal sealed partial class TopicTreeStore
{
    private readonly Guid _storeId = Guid.NewGuid();
    private MessageStore[] _selectedStores = [];
    private string? _selectionKey;
    private string? _selectionLabel;
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

    // A grouped row covers several real topics that are siblings under the store, so it selects a
    // set rather than one path. Paths that do not resolve are dropped; a selection that resolves
    // to nothing is refused, so a stale row cannot clear a live selection.
    public void SelectTopics(TopicSelection? selection)
    {
        lock (_storeSync)
        {
            if (selection is null)
            {
                if (_selectedStores.Length > 0)
                    InvalidateSelection();

                return;
            }

            var resolved = new List<MessageStore>(selection.Topics.Length);
            foreach (var topic in selection.Topics)
            {
                var normalized = Normalize(topic);
                if (normalized is null)
                    continue;

                var node = FindNode(normalized);
                // A path under one already selected is already covered, and counting it twice
                // would double the messages and the displayed count.
                if (node is null || resolved.Any(other => IsPathOrDescendant(node.FullTopic!, other.FullTopic!)))
                    continue;

                resolved.Add(node);
            }

            if (resolved.Count == 0)
                return;

            if (SameSelection(resolved, selection.Key))
                return;

            _selectedStores = [.. resolved];
            _selectionKey = selection.Key;
            _selectionLabel = selection.Label;
            _selectionGeneration++;
            _selectionContentVersion++;
        }
    }

    // The single-topic form keeps its own identity: the resolved node's path, not the caller's
    // spelling, so callers still see the normalized topic they selected.
    public void SelectTopic(string? fullTopic)
    {
        if (fullTopic is null)
        {
            SelectTopics(null);
            return;
        }

        var normalized = Normalize(fullTopic);
        if (normalized is null)
            return;

        SelectTopics(TopicSelection.ForTopic(normalized));
    }

    public bool TryClearSelection(long treeVersion, SelectedTopicState expectedSelection)
    {
        lock (_storeSync)
        {
            if (Interlocked.Read(ref _globalVersion) != treeVersion
                || expectedSelection.Token.StoreId != _storeId
                || expectedSelection.Token.Generation != _selectionGeneration
                || _selectionKey is null
                || !string.Equals(_selectionKey, expectedSelection.Key, StringComparison.Ordinal))
                return false;

            InvalidateSelection();
            return true;
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
            if (_selectedStores.Length == 0 || limit == 0)
                messages = [];
            else
            {
                var collected = new List<MqttMessage>();
                foreach (var store in _selectedStores)
                    CollectMessages(store, collected);
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
        _selectionKey,
        _selectedStores.Sum(store => store.MessageCount),
        _selectionLabel);

    // Re-selecting the same row must not look like a change, or a click that landed on the row
    // already selected would restart the message list and its own scroll position.
    private bool SameSelection(List<MessageStore> resolved, string key)
    {
        if (!string.Equals(_selectionKey, key, StringComparison.Ordinal)
            || resolved.Count != _selectedStores.Length)
            return false;

        return !resolved.Where((store, index) => !ReferenceEquals(store, _selectedStores[index])).Any();
    }

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
            children)
        {
            HasObservedDirectTopic = node.HasObservedDirectTopic,
            HasObservedCanonicalTopic =
                node.ObservedRawTopics?.Contains(node.FullTopic ?? string.Empty) == true
        };
    }

    private void InvalidateTopicTree() => _cachedTopicTree = null;

    private void InvalidateSelection()
    {
        _selectedStores = [];
        _selectionKey = null;
        _selectionLabel = null;
        _selectionGeneration++;
        _selectionContentVersion++;
    }

    private bool SelectionIncludes(MessageStore store)
    {
        if (_selectedStores.Length == 0)
            return false;

        var storePath = store.FullTopic;
        foreach (var selected in _selectedStores)
        {
            if (ReferenceEquals(selected, store))
                return true;

            var selectedPath = selected.FullTopic;
            if (selectedPath is null || storePath is null)
                continue;

            if (IsPathOrDescendant(storePath, selectedPath)
                || IsPathOrDescendant(selectedPath, storePath))
                return true;
        }

        return false;
    }

    private static bool IsPathOrDescendant(string path, string ancestor) =>
        path.Equals(ancestor, StringComparison.Ordinal)
        || (path.StartsWith(ancestor, StringComparison.Ordinal)
            && path.Length > ancestor.Length
            && path[ancestor.Length] == '/');
}
