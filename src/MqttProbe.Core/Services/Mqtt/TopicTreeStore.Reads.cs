using System.Collections.Immutable;
using MqttProbe.Core.Models.Mqtt;

namespace MqttProbe.Core.Services.Mqtt;

internal sealed partial class TopicTreeStore
{
    private readonly Guid _storeId = Guid.NewGuid();
    private MessageStore[] _selectedStores = [];
    private string? _selectionKey;
    private string? _selectionLabel;
    private SparkplugSelectionScope? _selectionScope;
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

    // A selection that resolves to nothing is refused, so a stale row cannot clear a live one.
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

            var resolved = selection.Scope is { } scope
                ? ResolveScope(scope)
                : ResolvePaths(selection.Topics);

            if (resolved.Count == 0)
                return;

            if (SameSelection(resolved, selection.Key, selection.Scope))
                return;

            _selectedStores = [.. resolved];
            _selectionKey = selection.Key;
            _selectionLabel = selection.Label;
            _selectionScope = selection.Scope;
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

    private List<MessageStore> ResolvePaths(ImmutableArray<string> topics)
    {
        var resolved = new List<MessageStore>(topics.Length);
        foreach (var topic in topics)
        {
            var normalized = Normalize(topic);
            if (normalized is null)
                continue;

            if (FindNode(normalized) is { } node)
                AddCovered(resolved, node);
        }

        return resolved;
    }

    private List<MessageStore> ResolveScope(SparkplugSelectionScope scope)
    {
        var resolved = new List<MessageStore>();
        if (FindNode(scope.GroupPath) is not { SubTopics: { } verbs })
            return resolved;

        foreach (var verbNode in verbs.Values)
        {
            var verb = verbNode.Topic ?? string.Empty;
            if (scope.Kind == SparkplugSelectionKind.Node && SparkplugTopicClassifier.IsNodeVerb(verb))
            {
                AddEndpoint(resolved, ChildOf(verbNode, scope.NodeId));
                continue;
            }

            if (!SparkplugTopicClassifier.IsDeviceVerb(verb) || ChildOf(verbNode, scope.NodeId) is not { SubTopics: { } devices })
                continue;

            foreach (var device in devices.Values)
            {
                if (scope.DeviceId is not null
                    && !string.Equals(device.Topic, scope.DeviceId, StringComparison.Ordinal))
                    continue;

                AddEndpoint(resolved, device);
            }
        }

        return resolved;
    }

    // The projection groups a tip only once a message arrived at its canonical path, so a scope
    // admits no endpoint that was ever reached only through an alias spelling.
    private static void AddEndpoint(List<MessageStore> resolved, MessageStore? endpoint)
    {
        if (endpoint?.FullTopic is not { } path || endpoint.ObservedRawTopics?.Contains(path) != true)
            return;

        AddCovered(resolved, endpoint);
    }

    private static void AddCovered(List<MessageStore> resolved, MessageStore node)
    {
        var path = node.FullTopic;
        foreach (var retained in resolved)
        {
            if (path is not null && retained.FullTopic is { } retainedPath
                && IsPathOrDescendant(path, retainedPath))
                return;
        }

        resolved.RemoveAll(retained => ReferenceEquals(retained, node)
            || (path is not null && retained.FullTopic is { } retainedPath && IsDescendantPath(retainedPath, path)));
        resolved.Add(node);
    }

    // Re-selecting the same row must not look like a change, so a click on the already-selected
    // row does not restart the message list. Store order is not meaningful, so compare as a set.
    private bool SameSelection(List<MessageStore> resolved, string key, SparkplugSelectionScope? scope)
    {
        if (!string.Equals(_selectionKey, key, StringComparison.Ordinal)
            || !Equals(_selectionScope, scope)
            || resolved.Count != _selectedStores.Length)
            return false;

        var retained = new HashSet<MessageStore>(_selectedStores);
        return resolved.All(retained.Contains);
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
        _selectionScope = null;
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

    // A scoped selection holds endpoints, so a device or verb whose first canonical message
    // arrives after the click is not under any selected store yet. Append it here, where the
    // spelling has just been recorded, rather than rescanning the tree for every message.
    private void AppendToScopedSelection(MessageStore store)
    {
        if (_selectionScope is not { } scope || !IsScopeEndpoint(store, scope) || _selectedStores.Contains(store))
            return;

        _selectedStores = [.. _selectedStores, store];
    }

    private static bool IsScopeEndpoint(MessageStore store, SparkplugSelectionScope scope)
    {
        if (store.FullTopic is not { } path || store.ObservedRawTopics?.Contains(path) != true)
            return false;

        var prefix = scope.GroupPath + "/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var segments = path[prefix.Length..].Split('/');
        if (segments.Length == 2)
        {
            return scope.Kind == SparkplugSelectionKind.Node
                && SparkplugTopicClassifier.IsNodeVerb(segments[0])
                && string.Equals(segments[1], scope.NodeId, StringComparison.Ordinal);
        }

        return segments.Length == 3
            && SparkplugTopicClassifier.IsDeviceVerb(segments[0])
            && string.Equals(segments[1], scope.NodeId, StringComparison.Ordinal)
            && (scope.DeviceId is null || string.Equals(segments[2], scope.DeviceId, StringComparison.Ordinal));
    }

    private static MessageStore? ChildOf(MessageStore parent, string key) =>
        parent.SubTopics is not null && parent.SubTopics.TryGetValue(key, out var child) ? child : null;

    private static bool IsPathOrDescendant(string path, string ancestor) =>
        path.Equals(ancestor, StringComparison.Ordinal)
        || IsDescendantPath(path, ancestor);

    private static bool IsDescendantPath(string path, string ancestor) =>
        path.Length > ancestor.Length
        && path.StartsWith(ancestor, StringComparison.Ordinal)
        && path[ancestor.Length] == '/';
}
