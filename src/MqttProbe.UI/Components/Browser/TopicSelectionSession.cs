using System.Collections.Immutable;
using MqttProbe.Core.Models.Mqtt;
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
        bool selectionMode = false)
    {
        var snapshot = store.GetTopicTreeSnapshot();
        var selection = store.GetSelectedTopicState();

        if (!force
            && _acceptedTreeVersion == snapshot.Version
            && _acceptedSelection is { } acceptedSelection
            && selection.Token.IsSameSelection(acceptedSelection))
            return null;

        List<TopicTreeRowModel> rows;
        bool selectionRepresented;
        if (selectionMode)
        {
            rows = TopicTreeProjection.CreateRows(
                snapshot.Roots, filter, expandedPaths, grouped,
                selectionPath: null, out _, selectionMode: true);
            selectionRepresented = true;
        }
        else
        {
            // Only a single-topic key names a real path. A branch or multi key is an identity,
            // so treating it as a path would find no row and clear a live selection.
            var selectionPath = selection.Mode == TopicSelectionMode.SingleTopic ? selection.Key : null;
            rows = TopicTreeProjection.CreateRows(
                snapshot.Roots, filter, expandedPaths, grouped, selectionPath, out selectionRepresented);
        }

        if (selection.Mode == TopicSelectionMode.SingleTopic
            && selection.Key is not null && !selectionRepresented)
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
}
