using System.Collections.Immutable;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Models.Sparkplug;
using MqttProbe.Core.Services.Mqtt;

namespace MqttProbe.UI.Components.Browser;

internal interface ITopicSelectionStore
{
    public TopicTreeSnapshot GetTopicTreeSnapshot();

    public SelectedTopicState GetSelectedTopicState();

    public void SelectTopics(TopicSelection? selection);

    public bool TryClearSelection(long treeVersion, SelectedTopicState expectedSelection);

    public bool TryReplaceSelection(
        long treeVersion, SelectedTopicState expectedSelection, TopicSelection? replacement);

    public Task<SelectedMessagesSnapshot?> GetSelectedMessagesAsync(SelectedTopicToken expectedSelection, int limit);
}

internal sealed class MessageStoreSelectionAdapter(IMessageStoreManager manager) : ITopicSelectionStore
{
    public TopicTreeSnapshot GetTopicTreeSnapshot() => manager.GetTopicTreeSnapshot();

    public SelectedTopicState GetSelectedTopicState() => manager.GetSelectedTopicState();

    public void SelectTopics(TopicSelection? selection) => manager.SelectTopics(selection);

    public bool TryClearSelection(long treeVersion, SelectedTopicState expectedSelection) =>
        manager.TryClearSelection(treeVersion, expectedSelection);

    public bool TryReplaceSelection(
        long treeVersion, SelectedTopicState expectedSelection, TopicSelection? replacement) =>
        manager.TryReplaceSelection(treeVersion, expectedSelection, replacement);

    public Task<SelectedMessagesSnapshot?> GetSelectedMessagesAsync(SelectedTopicToken expectedSelection, int limit) =>
        manager.GetSelectedMessagesAsync(expectedSelection, limit);
}

internal sealed record TopicSelectionFrame(
    IReadOnlyList<TopicTreeRowModel> Rows,
    SelectedTopicState Selection,
    int RootCount,
    ImmutableArray<TopicNodeSnapshot> Roots);

internal sealed class TopicSelectionSession(ITopicSelectionStore store)
{
    private long? _acceptedTreeVersion;
    private SelectedTopicToken? _acceptedSelection;

    public TopicSelectionFrame? Frame { get; private set; }

    public TopicSelectionFrame? Refresh(
        string? filter,
        IReadOnlySet<string> expandedPaths,
        bool grouped,
        bool force = false,
        bool selectionMode = false,
        Func<SparkplugSelectionScope, IReadOnlyList<SpbMetricSnapshot>>? metricSource = null)
    {
        var snapshot = store.GetTopicTreeSnapshot();
        var selection = store.GetSelectedTopicState();

        if (!force
            && _acceptedTreeVersion == snapshot.Version
            && _acceptedSelection is { } acceptedSelection
            && selection.Token.IsSameSelection(acceptedSelection))
            return null;

        var metricProjection = metricSource is null ? null : new MetricProjection(metricSource);

        List<TopicTreeRowModel> rows;
        bool selectionRepresented;
        if (selectionMode)
        {
            rows = TopicTreeProjection.CreateRows(
                snapshot.Roots, filter, expandedPaths, grouped,
                selectionPath: null, out _, out _, metrics: metricProjection);
            selectionRepresented = true;
        }
        else
        {
            // Only a single-topic key names a real path. A branch or multi key is an identity,
            // so treating it as a path would find no row and clear a live selection.
            var selectionPath = selection.Mode == TopicSelectionMode.SingleTopic ? selection.Key : null;
            rows = TopicTreeProjection.CreateRows(
                snapshot.Roots, filter, expandedPaths, grouped, selectionPath, out selectionRepresented,
                out _, metrics: metricProjection);
        }

        if (!TryPruneSelection(ref selection, snapshot, grouped, metricSource))
            return null;

        var pathSelectionGone = selection.Mode == TopicSelectionMode.SingleTopic
            && selection.Key is not null && !selectionRepresented;
        if (pathSelectionGone)
        {
            if (!store.TryClearSelection(snapshot.Version, selection))
                return null;

            selection = store.GetSelectedTopicState();
        }

        var frame = new TopicSelectionFrame(rows, selection, snapshot.Roots.Length, snapshot.Roots);
        _acceptedTreeVersion = snapshot.Version;
        _acceptedSelection = selection.Token;
        Frame = frame;
        return frame;
    }

    private bool TryPruneSelection(
        ref SelectedTopicState selection,
        TopicTreeSnapshot snapshot,
        bool grouped,
        Func<SparkplugSelectionScope, IReadOnlyList<SpbMetricSnapshot>>? metricSource)
    {
        if (selection.Mode != TopicSelectionMode.SparkplugMetric)
            return true;

        var survivors = PruneMetrics(selection, snapshot.Roots, grouped, metricSource);
        if (survivors.Length == selection.Metrics.Length)
            return true;

        var replacement = survivors.Length == 0
            ? null
            : TopicSelection.ForSparkplugMetrics(survivors);
        if (!store.TryReplaceSelection(snapshot.Version, selection, replacement))
            return false;

        selection = store.GetSelectedTopicState();
        return true;
    }

    // Pruning ignores search, collapse, offline state and zero stored matches. A metric survives
    // while its observed owner row remains and the current topology still lists its exact name.
    private static ImmutableArray<MqttSparkplugMetricTarget> PruneMetrics(
        SelectedTopicState selection,
        ImmutableArray<TopicNodeSnapshot> roots,
        bool grouped,
        Func<SparkplugSelectionScope, IReadOnlyList<SpbMetricSnapshot>>? metricSource)
    {
        var metrics = selection.Metrics;
        if (metrics.IsDefaultOrEmpty)
            return metrics;

        if (!grouped || metricSource is null)
            return [];

        var owners = TopicTreeProjection.RepresentedMetricOwners(
            roots, grouped, metrics.Select(metric => metric.Scope));
        var survivors = new List<MqttSparkplugMetricTarget>(metrics.Length);
        survivors.AddRange(metrics.Where(metric =>
            owners.Contains(metric.Scope)
            && metricSource(metric.Scope).Any(item =>
                string.Equals(item.Name, metric.MetricName, StringComparison.Ordinal))));

        return [.. survivors];
    }
}
