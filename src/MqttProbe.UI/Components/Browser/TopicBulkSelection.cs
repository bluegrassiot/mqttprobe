using System.Collections.Immutable;
using MqttProbe.Core.Models.Mqtt;

namespace MqttProbe.UI.Components.Browser;

// One selection shared by every mounted topic browser and by the actions strip. Bulk targets drive
// the store filter; a single browsing selection is mirrored as the branch it stands for without
// rewriting that selection.
internal sealed class TopicBulkSelection(ITopicSelectionStore store)
{
    private readonly Dictionary<string, TopicBranchTarget> _targets = new(StringComparer.Ordinal);
    private bool _adopting;
    private bool _passive;

    public event Action? Changed;

    public event Action<ImmutableArray<TopicBranchTarget>>? MessagesCleared;

    public void NotifyMessagesCleared(ImmutableArray<TopicBranchTarget> branches) =>
        MessagesCleared?.Invoke(branches);

    public int Count => _targets.Count;

    public bool HasSelection => _targets.Count > 0;

    public bool IsPassive => _passive;

    private bool _coversWholeTree;

    // Set by the browser once it knows the current roots: the checked set then covers the whole
    // tree rather than only the branches that were picked one by one. The strip is a sibling of
    // the tree, so a change here has to notify it the same way a change of targets does.
    public bool CoversWholeTree
    {
        get => _coversWholeTree;
        set
        {
            if (_coversWholeTree == value)
                return;

            _coversWholeTree = value;
            Changed?.Invoke();
        }
    }

    public ImmutableArray<TopicBranchTarget> Branches =>
        [.. _targets.Values.OrderBy(TargetKey, StringComparer.Ordinal)];

    public string Summary => Describe(compact: false);

    public string CompactSummary => Describe(compact: true);

    private string Describe(bool compact)
    {
        if (_targets.Count == 0)
            return compact ? "None selected" : "No topics selected";

        if (_passive && _targets.Count == 1)
            return ScopeSummary(_targets.Values.First(), compact);

        if (CoversWholeTree)
            return compact ? "All selected" : "All topics selected";

        return CountText(_targets.Count, compact);
    }

    private static string CountText(int count, bool compact)
    {
        if (count == 1)
            return compact ? "1 selected" : "1 topic selected";

        return compact ? $"{count} selected" : $"{count} topics selected";
    }

    private static string ScopeSummary(TopicBranchTarget target, bool compact)
    {
        var label = TopicBranchTargetDisplay.Label(target);
        if (target.Kind == TopicBranchKind.Raw)
        {
            return compact
                ? $"Topic and subtopics: {label}"
                : $"Selected topic and its subtopics: {label}";
        }

        var kind = ScopeKind(target);
        return compact
            ? $"{char.ToUpperInvariant(kind[0])}{kind[1..]}: {label}"
            : $"Selected {kind}: {label}";
    }

    private static string ScopeKind(TopicBranchTarget target)
    {
        if (target.MessageType is not null)
            return "message type";

        return target.Scope?.Kind switch
        {
            SparkplugSelectionKind.Node => "node",
            SparkplugSelectionKind.Devices => "devices",
            SparkplugSelectionKind.Device => "device",
            _ => "scope"
        };
    }

    public bool Contains(TopicBranchTarget target) => _targets.ContainsKey(TargetKey(target));

    // Names the selected branch that covers a row, or null when the row stands for itself.
    public string? CoveringLabel(TopicBranchTarget target) =>
        _targets.Values
            .Where(selected => TopicBranchTargetRelations.Covers(selected, target))
            .Select(TopicBranchTargetDisplay.Label)
            .FirstOrDefault();

    // Replaces the checked set with the given roots, which is what the global checkbox asks for.
    public void SelectAll(IEnumerable<TopicBranchTarget> targets)
    {
        if (_passive)
            return;

        var next = new Dictionary<string, TopicBranchTarget>(StringComparer.Ordinal);
        foreach (var target in targets)
            next[TargetKey(target)] = target;

        if (next.Count == _targets.Count && next.Keys.All(_targets.ContainsKey))
            return;

        _targets.Clear();
        foreach (var (key, target) in next)
            _targets[key] = target;

        ApplySelection();
        Changed?.Invoke();
    }

    public void Toggle(TopicBranchTarget target)
    {
        if (_passive)
            return;

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

    public void SetMultiSelect(bool enabled)
    {
        var passive = !enabled;
        if (_passive == passive)
            return;

        _passive = passive;
        _coversWholeTree = false;

        if (passive && _targets.Count > 1)
        {
            Clear();
            return;
        }

        // Promote the browsed topic filter so the next refresh adopts a branch, not an empty set.
        if (!passive && _targets.Count > 0
            && store.GetSelectedTopicState().Mode == TopicSelectionMode.SingleTopic)
            ApplySelection();

        Changed?.Invoke();
    }

    public bool AdoptSingle(SelectedTopicState selection)
    {
        if (!_passive)
            return false;

        // A metric selection has an identity key but no branch, so the browsed filter cannot be
        // mirrored as checked rows. Core owns the metric selection and must not be cleared here.
        if (selection.Mode == TopicSelectionMode.SparkplugMetric)
            return ReplaceTargets(ImmutableArray<TopicBranchTarget>.Empty);

        IEnumerable<TopicBranchTarget> branches = selection.Mode == TopicSelectionMode.Branches
            ? selection.Branches
            : SelectionPaths(selection).Select(path => new TopicBranchTarget(TopicBranchKind.Raw, Path: path));

        return ReplaceTargets(branches);
    }

    private static ImmutableArray<string> SelectionPaths(SelectedTopicState selection)
    {
        if (!selection.Paths.IsDefaultOrEmpty)
            return selection.Paths;

        return selection.Key is { Length: > 0 } key ? [key] : ImmutableArray<string>.Empty;
    }

    // Core resolves the request and can drop a target that no longer exists. Its snapshot is the
    // honest one, so the checked set follows it without asking Core again.
    public bool Adopt(IEnumerable<TopicBranchTarget> branches)
    {
        if (_adopting || _passive)
            return false;

        _adopting = true;
        try
        {
            return ReplaceTargets(branches);
        }
        finally
        {
            _adopting = false;
        }
    }

    private bool ReplaceTargets(IEnumerable<TopicBranchTarget> targets)
    {
        var incoming = new Dictionary<string, TopicBranchTarget>(StringComparer.Ordinal);
        foreach (var target in targets)
            incoming[TargetKey(target)] = target;

        if (incoming.Count == _targets.Count && incoming.Keys.All(_targets.ContainsKey))
            return false;

        _targets.Clear();
        foreach (var (key, target) in incoming)
            _targets[key] = target;

        Changed?.Invoke();
        return true;
    }

    public SelectedTopicState? CaptureBrowsedTopicForClear()
    {
        if (!_passive)
            return null;

        var state = store.GetSelectedTopicState();
        return state.Mode == TopicSelectionMode.SingleTopic && state.Key is not null
            ? state
            : null;
    }

    public void PreserveBrowsedTopicAfterClear(
        SelectedTopicState? browsed, IReadOnlyList<TopicBranchTarget> clearedBranches)
    {
        if (!_passive || browsed?.Key is not { } key || clearedBranches.Count == 0)
            return;
        if (store.GetSelectedTopicState().Key is not null)
            return;

        var captured = new TopicBranchTarget(TopicBranchKind.Raw, Path: key);
        if (!clearedBranches.Any(branch => TopicBranchTargetRelations.Covers(branch, captured)))
            return;

        store.SelectTopics(TopicSelection.ForBranches([captured]));
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
        if (_passive)
            return false;

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
