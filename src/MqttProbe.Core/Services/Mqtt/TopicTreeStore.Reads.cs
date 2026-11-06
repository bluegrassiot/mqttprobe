using System.Collections.Immutable;
using MqttProbe.Core.Models.Mqtt;

namespace MqttProbe.Core.Services.Mqtt;

internal sealed partial class TopicTreeStore
{
    private readonly Guid _storeId = Guid.NewGuid();
    private readonly HashSet<MessageStore> _selectedRoots = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<MessageStore> _selectedAncestors = new(ReferenceEqualityComparer.Instance);
    private string? _selectionKey;
    private string? _selectionLabel;
    private SparkplugSelectionScope? _selectionScope;
    private ImmutableArray<string> _selectionPaths = ImmutableArray<string>.Empty;
    private ImmutableArray<TopicBranchTarget> _selectionBranches = ImmutableArray<TopicBranchTarget>.Empty;
    private TopicSelectionMode _selectionMode;
    private bool _selectionRequiresCanonical;
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

    // A node selection that resolves to nothing is refused, so a stale row cannot clear a live one.
    // A branch selection is kept even when it matches nothing, so later arrivals are picked up.
    public void SelectTopics(TopicSelection? selection)
    {
        lock (_storeSync)
        {
            if (selection is null)
            {
                if (HasSelection())
                    InvalidateSelection();

                return;
            }

            if (selection.Mode == TopicSelectionMode.Branches)
            {
                ApplyBranchSelection(selection);
                return;
            }

            if (selection.Mode == TopicSelectionMode.MultipleTopics)
            {
                var exact = ResolveExactPaths(selection.Topics);
                if (exact.Count == 0)
                {
                    if (HasSelection())
                        InvalidateSelection();

                    return;
                }

                var exactPaths = SortedExactPaths(exact.Select(item => item.Path));
                var exactKey = TopicSelection.ComposeExactKey(exactPaths);
                var exactNodes = DistinctNodes(exact.Select(item => item.Node));
                if (SameSelection(exactNodes, exactKey, scope: null, requiresCanonical: false,
                        TopicSelectionMode.MultipleTopics))
                    return;

                ReplaceSelection(exactNodes, exactKey, ExactSelectionLabel(exactPaths), scope: null,
                    requiresCanonical: false, TopicSelectionMode.MultipleTopics, exactPaths);
                return;
            }

            var requiresCanonical = selection.RequireCanonicalTopics || selection.Scope is not null;
            var resolved = selection.Scope is { } scope
                ? ResolveScope(scope)
                : ResolvePaths(selection.Topics, requiresCanonical);

            if (resolved.Count == 0)
                return;

            if (requiresCanonical && resolved.Any(store => !IsCanonicallyObserved(store)))
                return;

            if (SameSelection(resolved, selection.Key, selection.Scope, requiresCanonical, TopicSelectionMode.SingleTopic))
                return;

            ReplaceSelection(resolved, selection.Key, selection.Label, selection.Scope, requiresCanonical,
                TopicSelectionMode.SingleTopic);
        }
    }

    public void SelectTopic(string? fullTopic)
    {
        if (fullTopic is null)
        {
            SelectTopics(null);
            return;
        }

        SelectTopics(TopicSelection.ForTopic(fullTopic));
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

            var collected = new List<MqttMessage>();
            int? branchCount = null;
            if (HasSelection() && limit != 0)
            {
                if (_selectionMode == TopicSelectionMode.MultipleTopics)
                {
                    foreach (var path in _selectionPaths)
                    {
                        var node = FindExactNode(path);
                        if (node?.Messages is not { IsEmpty: false } nodeMessages)
                            continue;

                        collected.AddRange(nodeMessages.Where(message => MatchesExactSpelling(message, node, path)));
                    }
                }
                else if (_selectionMode == TopicSelectionMode.Branches)
                {
                    CollectBranchMessages(collected);
                    branchCount = collected.Count;
                }
                else
                {
                    foreach (var store in _selectedRoots)
                        CollectMessages(store, collected);
                }
            }

            state = CaptureSelectedTopicState(branchCount);
            messages = collected.ToArray();
        }

        Array.Sort(messages, static (left, right) => right.DateTimeReceived.CompareTo(left.DateTimeReceived));
        if (messages.Length > limit)
            Array.Resize(ref messages, limit);

        return new SelectedMessagesSnapshot(state, limit, ImmutableArray.CreateRange(messages));
    }

    private SelectedTopicState CaptureSelectedTopicState(int? branchMessageCount = null)
    {
        var paths = _selectionPaths;
        var messageCount = _selectionMode switch
        {
            TopicSelectionMode.MultipleTopics => CountExactMessages(),
            TopicSelectionMode.Branches => branchMessageCount ?? CountBranchMessages(),
            _ => _selectedRoots.Sum(store => store.MessageCount)
        };

        return new SelectedTopicState(
            new SelectedTopicToken(_storeId, _selectionGeneration, _selectionContentVersion),
            _selectionKey,
            messageCount,
            _selectionLabel)
        {
            Paths = paths,
            Mode = _selectionMode,
            Branches = _selectionBranches
        };
    }

    // A canonical request is validated per input topic before overlap normalization, so a missing,
    // invalid or non-canonical topic refuses the request even when an ancestor would cover it.
    private List<MessageStore> ResolvePaths(ImmutableArray<string> topics, bool requiresCanonical)
    {
        var resolved = new List<MessageStore>(topics.Length);
        foreach (var topic in topics)
        {
            var node = FindNode(topic);
            if (node is null)
            {
                if (requiresCanonical)
                    return [];

                continue;
            }

            if (requiresCanonical && !IsCanonicallyObserved(node))
                return [];

            AddCovered(resolved, node);
        }

        return resolved;
    }

    // Exact bulk selection keeps only the named concrete rows: no scope, no descendant
    // widening, no spelling normalization, and a path whose raw spelling is no longer
    // observed is dropped rather than guessed.
    private List<(string Path, MessageStore Node)> ResolveExactPaths(ImmutableArray<string> topics)
    {
        var resolved = new List<(string Path, MessageStore Node)>(topics.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var topic in topics)
        {
            if (!seen.Add(topic))
                continue;

            var node = FindNode(topic);
            if (node?.ObservedRawTopics?.Contains(topic) != true)
                continue;

            resolved.Add((topic, node));
        }

        return resolved;
    }

    private static ImmutableArray<string> SortedExactPaths(IEnumerable<string> paths) =>
        [.. paths.Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal)];

    private static List<MessageStore> DistinctNodes(IEnumerable<MessageStore> nodes)
        => nodes.Distinct<MessageStore>(ReferenceEqualityComparer.Instance).ToList();

    private static string ExactSelectionLabel(ImmutableArray<string> paths) =>
        paths.Length == 1 ? paths[0] : $"{paths.Length} topics selected";

    private int CountExactMessages()
    {
        var count = 0;
        foreach (var path in _selectionPaths)
        {
            var node = FindExactNode(path);
            if (node?.Messages is not { IsEmpty: false } messages)
                continue;

            count += messages.Count(message => MatchesExactSpelling(message, node, path));
        }

        return count;
    }

    private MessageStore? FindExactNode(string path) => FindNode(path);

    private static bool MatchesExactSpelling(MqttMessage message, MessageStore store, string path) =>
        string.Equals(message.Topic ?? store.FullTopic ?? string.Empty, path, StringComparison.Ordinal);

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

    // A scope admits only endpoints observed at their canonical path, and they are disjoint by
    // construction, so they need no overlap normalization.
    private static void AddEndpoint(List<MessageStore> resolved, MessageStore? endpoint)
    {
        if (endpoint is not null && IsCanonicallyObserved(endpoint))
            resolved.Add(endpoint);
    }

    private static bool IsCanonicallyObserved(MessageStore store) =>
        store.FullTopic is { } path && store.ObservedRawTopics?.Contains(path) == true;

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

    private bool SameSelection(
        List<MessageStore> resolved, string key, SparkplugSelectionScope? scope, bool requiresCanonical,
        TopicSelectionMode mode) =>
        _selectionMode == mode
        && string.Equals(_selectionKey, key, StringComparison.Ordinal)
        && Equals(_selectionScope, scope)
        && _selectionRequiresCanonical == requiresCanonical
        && _selectedRoots.SetEquals(resolved);

    private void ReplaceSelection(
        List<MessageStore> resolved, string key, string? label, SparkplugSelectionScope? scope, bool requiresCanonical,
        TopicSelectionMode mode, ImmutableArray<string> exactPaths = default)
    {
        _selectedRoots.Clear();
        _selectedAncestors.Clear();
        foreach (var store in resolved)
        {
            _selectedRoots.Add(store);
            IndexAncestors(store);
        }

        _selectionKey = key;
        _selectionLabel = label;
        _selectionScope = scope;
        _selectionPaths = exactPaths.IsDefault ? SnapshotPaths(resolved) : exactPaths;
        _selectionBranches = ImmutableArray<TopicBranchTarget>.Empty;
        _selectionMode = mode;
        _selectionRequiresCanonical = requiresCanonical;
        _selectionGeneration++;
        _selectionContentVersion++;
    }

    private static ImmutableArray<string> SnapshotPaths(IEnumerable<MessageStore> stores) =>
        [.. stores.Select(store => store.FullTopic)
            .OfType<string>()
            .OrderBy(path => path, StringComparer.Ordinal)];

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
                node.ObservedRawTopics?.Contains(node.FullTopic ?? string.Empty) == true,
            ObservedTopicPaths = node.ObservedRawTopics is { Count: > 0 } observed
                ? [.. observed.OrderBy(path => path, StringComparer.Ordinal)]
                : ImmutableArray<string>.Empty
        };
    }

    private void InvalidateTopicTree() => _cachedTopicTree = null;

    private void InvalidateSelection()
    {
        _selectedRoots.Clear();
        _selectedAncestors.Clear();
        _selectionKey = null;
        _selectionLabel = null;
        _selectionScope = null;
        _selectionPaths = ImmutableArray<string>.Empty;
        _selectionBranches = ImmutableArray<TopicBranchTarget>.Empty;
        _selectionMode = TopicSelectionMode.SingleTopic;
        _selectionRequiresCanonical = false;
        _selectionGeneration++;
        _selectionContentVersion++;
    }

    // The walk up tests the root set, not the ancestor set: an ancestor-set hit would match a
    // sibling under a shared ancestor and invalidate unrelated traffic.
    private bool SelectionIncludes(MessageStore store)
    {
        if (_selectedRoots.Count == 0)
            return false;

        if (_selectedAncestors.Contains(store))
            return true;

        for (var node = store; node is not null; node = node.Parent)
        {
            if (_selectedRoots.Contains(node))
                return true;
        }

        return false;
    }

    // A scoped selection holds endpoints, so one first observed after the click is not in the
    // set yet; add it as its spelling is recorded.
    private void AppendToScopedSelection(MessageStore store)
    {
        if (_selectionScope is not { } scope || !IsScopeEndpoint(store, scope) || !_selectedRoots.Add(store))
            return;

        IndexAncestors(store);
        _selectionPaths = SnapshotPaths(_selectedRoots);
    }

    // Break once an ancestor is already indexed: its whole chain was indexed with it.
    private void IndexAncestors(MessageStore store)
    {
        for (var node = store.Parent; node is not null; node = node.Parent)
        {
            if (!_selectedAncestors.Add(node))
                break;
        }
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
