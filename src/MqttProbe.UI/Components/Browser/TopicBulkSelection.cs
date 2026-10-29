using System.Collections.Immutable;
using MqttProbe.Core.Models.Mqtt;

namespace MqttProbe.UI.Components.Browser;

// One bulk selection shared by every mounted topic browser and by the actions strip. The checked
// paths are the identity: Core resolves them, and the displayed order never enters the key.
internal sealed class TopicBulkSelection(ITopicSelectionStore store)
{
    private readonly SortedSet<string> _paths = new(StringComparer.Ordinal);
    private bool _adopting;

    public event Action? Changed;

    public event Action<ImmutableArray<string>>? MessagesCleared;

    public void NotifyMessagesCleared(ImmutableArray<string> topics) => MessagesCleared?.Invoke(topics);

    public bool IsActive { get; private set; }

    public int Count => _paths.Count;

    public bool HasSelection => _paths.Count > 0;

    public ImmutableArray<string> Paths => [.. _paths];

    public string Summary => Count == 1 ? "1 topic selected" : $"{Count} topics selected";

    public bool Contains(string path) => _paths.Contains(path);

    public bool ContainsAll(IEnumerable<string> paths) => paths.All(_paths.Contains);

    public bool ContainsAny(IEnumerable<string> paths) => paths.Any(_paths.Contains);

    public void Enter(IEnumerable<string> seed)
    {
        _paths.Clear();
        foreach (var path in seed)
            _paths.Add(path);

        IsActive = true;
        ApplySelection();
        Changed?.Invoke();
    }

    public void Exit()
    {
        IsActive = false;
        if (_paths.Count == 0)
        {
            Changed?.Invoke();
            return;
        }

        _paths.Clear();
        ApplySelection();
        Changed?.Invoke();
    }

    public void Toggle(string path) => ToggleAll([path]);

    // A row can carry more than one observed spelling for the same node. Selecting the row
    // selects exactly those raw spellings; unchecking removes all of them.
    public void ToggleAll(IEnumerable<string> paths)
    {
        var targets = paths.Distinct(StringComparer.Ordinal).ToList();
        if (targets.Count == 0)
            return;

        var remove = targets.All(_paths.Contains);
        foreach (var path in targets)
        {
            if (remove)
                _paths.Remove(path);
            else
                _paths.Add(path);
        }

        ApplySelection();
        Changed?.Invoke();
    }

    public void Clear()
    {
        if (_paths.Count == 0)
            return;

        _paths.Clear();
        ApplySelection();
        Changed?.Invoke();
    }

    public bool Reconcile(Func<string, bool> stillRepresents)
    {
        var stale = _paths.Where(path => !stillRepresents(path)).ToList();
        if (stale.Count == 0)
            return false;

        foreach (var path in stale)
            _paths.Remove(path);

        ApplySelection();
        Changed?.Invoke();
        return true;
    }

    // Core resolves the request and can drop a path that no longer exists. Its snapshot is the
    // honest one, so the checked set follows it without asking Core again.
    public bool Adopt(IEnumerable<string> paths)
    {
        if (_adopting)
            return false;

        var incoming = new SortedSet<string>(paths, StringComparer.Ordinal);
        if (incoming.SetEquals(_paths))
            return false;

        _paths.Clear();
        foreach (var path in incoming)
            _paths.Add(path);

        _adopting = true;
        try
        {
            Changed?.Invoke();
        }
        finally
        {
            _adopting = false;
        }

        return true;
    }

    public void Remove(IEnumerable<string> paths)
    {
        var removed = false;
        foreach (var path in paths)
            removed |= _paths.Remove(path);

        if (!removed)
            return;

        ApplySelection();
        Changed?.Invoke();
    }

    private void ApplySelection()
    {
        if (_paths.Count == 0)
        {
            store.SelectTopics(null);
            return;
        }

        store.SelectTopics(TopicSelection.ForExactTopics(Paths));
    }
}
