using System.Collections.Immutable;
using MqttProbe.Core.Models.Mqtt;

namespace MqttProbe.UI.Components.Browser;

// One bulk selection shared by every mounted topic browser and by the actions strip. The targets
// are branch descriptors: the store resolves them, so a selected scope keeps covering members
// that arrive later.
internal sealed class TopicBulkSelection(ITopicSelectionStore store)
{
    private readonly Dictionary<string, TopicBranchTarget> _targets = new(StringComparer.Ordinal);
    private bool _adopting;

    public event Action? Changed;

    public event Action<ImmutableArray<TopicBranchTarget>>? MessagesCleared;

    public void NotifyMessagesCleared(ImmutableArray<TopicBranchTarget> branches) =>
        MessagesCleared?.Invoke(branches);

    public bool IsActive { get; private set; }

    public int Count => _targets.Count;

    public bool HasSelection => _targets.Count > 0;

    public ImmutableArray<TopicBranchTarget> Branches =>
        [.. _targets.Values.OrderBy(TargetKey, StringComparer.Ordinal)];

    public ImmutableArray<string> RawPaths =>
        [.. Branches
            .Where(target => target.Kind == TopicBranchKind.Raw && target.Path is not null)
            .Select(target => target.Path!)];

    public string Summary => Count == 1 ? "1 topic selected" : $"{Count} topics selected";

    public bool Contains(TopicBranchTarget target) => _targets.ContainsKey(TargetKey(target));

    // Names the selected branch that covers a row, or null when the row stands for itself.
    public string? CoveringLabel(TopicBranchTarget target) =>
        _targets.Values
            .Where(selected => TopicBranchTargetRelations.Covers(selected, target))
            .Select(TopicBranchTargetDisplay.Label)
            .FirstOrDefault();

    public void Enter(IEnumerable<TopicBranchTarget> seed)
    {
        _targets.Clear();
        foreach (var target in seed)
            _targets[TargetKey(target)] = target;

        IsActive = true;
        ApplySelection();
        Changed?.Invoke();
    }

    public void Exit()
    {
        IsActive = false;
        if (_targets.Count == 0)
        {
            Changed?.Invoke();
            return;
        }

        _targets.Clear();
        ApplySelection();
        Changed?.Invoke();
    }

    public void Toggle(TopicBranchTarget target)
    {
        if (_targets.Remove(TargetKey(target)))
        {
            ApplySelection();
        }
        else
        {
            _targets[TargetKey(target)] = target;
            RemoveCoveredDescendants(target);
            ApplySelection();
        }

        Changed?.Invoke();
    }

    public void Clear()
    {
        if (_targets.Count == 0)
            return;

        _targets.Clear();
        ApplySelection();
        Changed?.Invoke();
    }

    // Core resolves the request and can drop a target that no longer exists. Its snapshot is the
    // honest one, so the checked set follows it without asking Core again.
    public bool Adopt(IEnumerable<TopicBranchTarget> branches)
    {
        if (_adopting)
            return false;

        var incoming = new Dictionary<string, TopicBranchTarget>(StringComparer.Ordinal);
        foreach (var target in branches)
            incoming[TargetKey(target)] = target;

        if (incoming.Count == _targets.Count && incoming.Keys.All(_targets.ContainsKey))
            return false;

        _targets.Clear();
        foreach (var (key, target) in incoming)
            _targets[key] = target;

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

    public void Remove(IEnumerable<TopicBranchTarget> branches)
    {
        var removed = false;
        foreach (var target in branches)
            removed |= _targets.Remove(TargetKey(target));

        if (!removed)
            return;

        ApplySelection();
        Changed?.Invoke();
    }

    public bool Reconcile(Func<TopicBranchTarget, bool> stillRepresents)
    {
        var stale = _targets.Values.Where(target => !stillRepresents(target)).ToList();
        if (stale.Count == 0)
            return false;

        foreach (var target in stale)
            _targets.Remove(TargetKey(target));

        ApplySelection();
        Changed?.Invoke();
        return true;
    }

    private void RemoveCoveredDescendants(TopicBranchTarget target)
    {
        var covered = _targets
            .Where(pair => TopicBranchTargetRelations.IsUnder(pair.Value, target))
            .Select(pair => pair.Key)
            .ToList();

        foreach (var key in covered)
            _targets.Remove(key);
    }

    private void ApplySelection()
    {
        if (_targets.Count == 0)
        {
            store.SelectTopics(null);
            return;
        }

        store.SelectTopics(TopicSelection.ForBranches(Branches));
    }

    private static string TargetKey(TopicBranchTarget target)
    {
        // Core's branch identity length-prefixes every field, so no topic or id can alias another.
        return TopicSelection.ForBranches([target]).Key;
    }
}
