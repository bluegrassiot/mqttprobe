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

    public Task<SelectedMessagesSnapshot?> GetSelectedMessagesAsync(SelectedTopicToken expectedSelection, int limit);
}

internal sealed class MessageStoreSelectionAdapter(IMessageStoreManager manager) : ITopicSelectionStore
{
    public TopicTreeSnapshot GetTopicTreeSnapshot() => manager.GetTopicTreeSnapshot();

    public SelectedTopicState GetSelectedTopicState() => manager.GetSelectedTopicState();

    public void SelectTopics(TopicSelection? selection) => manager.SelectTopics(selection);

    public bool TryClearSelection(long treeVersion, SelectedTopicState expectedSelection) =>
        manager.TryClearSelection(treeVersion, expectedSelection);

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

        SparkplugSelectionScope? selectedMetricOwner = null;
        if (selection.Mode == TopicSelectionMode.SparkplugMetric)
            selectedMetricOwner = selection.Metric?.Scope;

        var metricProjection = metricSource is null
            ? null
            : new MetricProjection(metricSource, selectedMetricOwner);

        List<TopicTreeRowModel> rows;
        bool selectionRepresented;
        bool metricOwnerRepresented;
        if (selectionMode)
        {
            rows = TopicTreeProjection.CreateRows(
                snapshot.Roots, filter, expandedPaths, grouped,
                selectionPath: null, out _, out metricOwnerRepresented, selectionMode: true,
                metrics: metricProjection);
            selectionRepresented = true;
        }
        else
        {
            // Only a single-topic key names a real path. A branch or multi key is an identity,
            // so treating it as a path would find no row and clear a live selection.
            var selectionPath = selection.Mode == TopicSelectionMode.SingleTopic ? selection.Key : null;
            rows = TopicTreeProjection.CreateRows(
                snapshot.Roots, filter, expandedPaths, grouped, selectionPath, out selectionRepresented,
                out metricOwnerRepresented, metrics: metricProjection);
        }

        var pathSelectionGone = selection.Mode == TopicSelectionMode.SingleTopic
            && selection.Key is not null && !selectionRepresented;
        var metricSelectionGone = selection.Mode == TopicSelectionMode.SparkplugMetric
            && !MetricStillRepresented(selection, grouped, selectionMode, metricOwnerRepresented, metricSource);

        if (pathSelectionGone || metricSelectionGone)
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

    // A metric stays selected through collapsing, filtering and zero matches. Only leaving the
    // single browsing mode, losing the observed owner row, or the metric leaving the topology
    // after a birth replacement or purge ends it.
    private static bool MetricStillRepresented(
        SelectedTopicState selection,
        bool grouped,
        bool selectionMode,
        bool ownerRepresented,
        Func<SparkplugSelectionScope, IReadOnlyList<SpbMetricSnapshot>>? metricSource)
    {
        if (!grouped || selectionMode || !ownerRepresented || metricSource is null
            || selection.Metric is not { } metric)
            return false;

        return metricSource(metric.Scope)
            .Any(item => string.Equals(item.Name, metric.MetricName, StringComparison.Ordinal));
    }
}
