using System.Collections.Immutable;
using MqttProbe.Core.Models.Mqtt;

namespace MqttProbe.Core.Services.Mqtt;

internal sealed partial class TopicTreeStore
{
    private int _branchMessageCount;
    private long _branchCountGeneration = -1;
    private long _branchCountContentVersion = -1;

    private bool HasSelection() =>
        _selectionMode == TopicSelectionMode.Branches
            ? !_selectionBranches.IsDefaultOrEmpty
            : _selectedRoots.Count > 0;

    private bool MatchesAnyBranch(string topic) =>
        _selectionBranches.Any(branch => TopicBranchMatcher.Matches(branch, topic));

    private void ApplyBranchSelection(TopicSelection selection)
    {
        if (selection.Branches.IsDefaultOrEmpty)
        {
            if (HasSelection())
                InvalidateSelection();

            return;
        }

        var branches = selection.Branches.Distinct().ToImmutableArray();
        var branchKey = TopicSelection.ComposeBranchKey(branches);
        if (_selectionMode == TopicSelectionMode.Branches
            && string.Equals(_selectionKey, branchKey, StringComparison.Ordinal))
            return;

        ReplaceBranchSelection(branches, branchKey, selection.Label);
    }

    // Branches match at read time, so one whose node does not exist yet still picks up later arrivals.
    // Every stored message carries its node's published path, so one match per node covers them all.
    private void CollectBranchMessages(List<MqttMessage> result)
    {
        foreach (var root in _messageStores.Values)
            CollectBranchNode(root, result);
    }

    private void CollectBranchNode(MessageStore node, List<MqttMessage> result)
    {
        if (MatchesAnyBranch(node.FullTopic ?? string.Empty) && node.Messages is { IsEmpty: false } messages)
            result.AddRange(messages);

        if (node.SubTopics is null)
            return;

        foreach (var child in node.SubTopics.Values)
            CollectBranchNode(child, result);
    }

    private int CountBranchMessages()
    {
        if (_branchCountGeneration == _selectionGeneration
            && _branchCountContentVersion == _selectionContentVersion)
            return _branchMessageCount;

        _branchMessageCount = _messageStores.Values.Sum(CountBranchNode);
        _branchCountGeneration = _selectionGeneration;
        _branchCountContentVersion = _selectionContentVersion;
        return _branchMessageCount;
    }

    private int CountBranchNode(MessageStore node)
    {
        var count = MatchesAnyBranch(node.FullTopic ?? string.Empty) && node.Messages is { IsEmpty: false } messages
            ? messages.Count
            : 0;

        return node.SubTopics is null ? count : count + node.SubTopics.Values.Sum(CountBranchNode);
    }

    private void ReplaceBranchSelection(ImmutableArray<TopicBranchTarget> branches, string key, string? label)
    {
        _selectedRoots.Clear();
        _selectedAncestors.Clear();
        _selectionBranches = branches;
        _selectionKey = key;
        _selectionLabel = label;
        _selectionScope = null;
        _selectionPaths = ImmutableArray<string>.Empty;
        _selectionMode = TopicSelectionMode.Branches;
        _selectionRequiresCanonical = false;
        _selectionGeneration++;
        _selectionContentVersion++;
    }

    private bool SelectionCovers(MessageStore store, string rawTopic) =>
        SelectionIncludes(store)
        || (_selectionMode == TopicSelectionMode.Branches && MatchesAnyBranch(rawTopic));
}
