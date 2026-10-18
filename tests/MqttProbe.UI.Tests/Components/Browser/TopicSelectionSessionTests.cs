using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Mqtt;

namespace MqttProbe.UI.Tests.Components.Browser;

[TestFixture]
public sealed class TopicSelectionSessionTests
{
    [Test]
    public void GroupedPurgeClearsSelectionAndCommitsPrunedRows()
    {
        var adapter = CreateAdapter();
        adapter.Add("spBv1.0/g/NDATA/n1", Message("n1", 1));
        adapter.Add("spBv1.0/g/NDATA/n2", Message("n2", 2));
        adapter.Store.SelectTopics(TopicSelection.ForTopic("spBv1.0/g/NDATA/n1"));
        var session = new TopicSelectionSession(adapter);
        session.Refresh(null, EmptyExpanded(), grouped: true).Should().NotBeNull();

        adapter.Store.RemoveMatchingTopic("spBv1.0/g/NDATA/n1");
        var frame = session.Refresh(null, EmptyExpanded(), grouped: true);

        frame.Should().NotBeNull();
        frame.Selection.Key.Should().BeNull();
        frame.Rows.Should().NotContain(row => row.FullPath == "spBv1.0/g/NDATA/n1");
    }

    [Test]
    public void ValidGroupedTipSurvivesReconciliation()
    {
        var adapter = CreateAdapter();
        adapter.Add("spBv1.0/g/NDATA/n1", Message("n1", 1));
        adapter.Store.SelectTopics(TopicSelection.ForTopic("spBv1.0/g/NDATA/n1"));
        var session = new TopicSelectionSession(adapter);

        var frame = session.Refresh(null, EmptyExpanded(), grouped: true);

        frame.Should().NotBeNull();
        frame.Selection.Key.Should().Be("spBv1.0/g/NDATA/n1");
        adapter.Store.GetSelectedTopicState().Key.Should().Be("spBv1.0/g/NDATA/n1");
    }

    [Test]
    public void PrunedTopicClearsSelection()
    {
        var adapter = CreateAdapter();
        adapter.Add("root/selected", Message("selected", 1));
        adapter.Add("other", Message("other", 2));
        adapter.Store.SelectTopics(TopicSelection.ForTopic("root/selected"));
        var session = new TopicSelectionSession(adapter);
        session.Refresh(null, EmptyExpanded(), grouped: false).Should().NotBeNull();

        adapter.Store.RemoveMatchingTopic("root/selected");
        var frame = session.Refresh(null, EmptyExpanded(), grouped: false);

        frame.Should().NotBeNull();
        frame.Selection.Key.Should().BeNull();
        frame.Rows.Should().NotContain(row => row.FullPath == "root/selected");
    }

    [Test]
    public void RejectedConditionalClearLeavesFrameAndCursorUntouchedThenRecovers()
    {
        var adapter = CreateAdapter();
        adapter.Add("root/selected", Message("selected", 1));
        adapter.Add("other", Message("other", 2));
        adapter.Store.SelectTopics(TopicSelection.ForTopic("root/selected"));
        var session = new TopicSelectionSession(adapter);
        var original = session.Refresh(null, EmptyExpanded(), grouped: false)!;

        adapter.ReturnSnapshotOnce(new TopicTreeSnapshot(adapter.Store.Version, ImmutableArray<TopicNodeSnapshot>.Empty));
        adapter.BeforeClear = () => adapter.Add("root/selected", Message("returned", 3));
        session.Refresh(null, EmptyExpanded(), grouped: false, force: true).Should().BeNull();
        session.Frame.Should().BeSameAs(original, "a rejected clear must not commit candidate rows");
        session.Frame!.Rows.Should().Contain(row => row.FullPath == "root");

        adapter.BeforeClear = null;
        var recovered = session.Refresh(null, EmptyExpanded(), grouped: false);
        recovered.Should().NotBeNull();
        recovered!.Selection.Key.Should().Be("root/selected");
        recovered.Rows.Should().Contain(row => row.FullPath == "root");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SelectionChangedBeforeClearRejectsWithoutCommitting(bool awayAndBack)
    {
        var adapter = CreateAdapter();
        adapter.Add("root/selected", Message("selected", 1));
        adapter.Add("root/other", Message("other", 2));
        adapter.Store.SelectTopics(TopicSelection.ForTopic("root/selected"));
        var session = new TopicSelectionSession(adapter);
        var original = session.Refresh(null, EmptyExpanded(), grouped: false)!;
        adapter.ReturnSnapshotOnce(new TopicTreeSnapshot(adapter.Store.Version, ImmutableArray<TopicNodeSnapshot>.Empty));
        adapter.BeforeClear = () =>
        {
            adapter.Store.SelectTopics(TopicSelection.ForTopic("root/other"));
            if (awayAndBack)
                adapter.Store.SelectTopics(TopicSelection.ForTopic("root/selected"));
        };

        session.Refresh(null, EmptyExpanded(), grouped: false, force: true).Should().BeNull();
        session.Frame.Should().BeSameAs(original);
        session.Frame!.Rows.Should().Contain(row => row.FullPath == "root");
    }

    [Test]
    public void FilterAndCollapsedExpansionDoNotInvalidateRepresentedSelection()
    {
        var adapter = CreateAdapter();
        adapter.Add("root/selected", Message("selected", 1));
        adapter.Add("root/other", Message("other", 2));
        adapter.Store.SelectTopics(TopicSelection.ForTopic("root/selected"));
        var session = new TopicSelectionSession(adapter);

        var frame = session.Refresh("other", EmptyExpanded(), grouped: false);

        frame.Should().NotBeNull();
        frame!.Selection.Key.Should().Be("root/selected");
        adapter.Store.GetSelectedTopicState().Key.Should().Be("root/selected");
    }

    [Test]
    public void ContentVersionChangeDoesNotChangeSelectionIdentityForSkipCursor()
    {
        var adapter = CreateAdapter();
        adapter.Add("root/selected", Message("selected", 1));
        adapter.Store.SelectTopics(TopicSelection.ForTopic("root/selected"));
        var session = new TopicSelectionSession(adapter);
        var first = session.Refresh(null, EmptyExpanded(), grouped: false)!;
        var snapshot = adapter.Store.GetTopicTreeSnapshot();

        adapter.Add("root/selected", Message("new content", 2));
        adapter.ReturnSnapshotOnce(snapshot);

        session.Refresh(null, EmptyExpanded(), grouped: false).Should().BeNull();
        session.Frame.Should().BeSameAs(first);
    }

    private static IReadOnlySet<string> EmptyExpanded() => new HashSet<string>(StringComparer.Ordinal);

    private static TopicTreeSelectionTestAdapter CreateAdapter()
    {
        var configuration = new AppConfiguration
        {
            Performance = new PerformanceSettings { MaxStoredMessages = 100, MaxTopicNodes = 100 }
        };
        var settings = Substitute.For<IPerformanceSettings>();
        settings.Performance.Returns(configuration.Performance);
        return new TopicTreeSelectionTestAdapter(new TopicTreeStore(settings, Substitute.For<ILogger>()));
    }

    private static MqttMessage Message(string payload, int second) =>
        new(payload, "unused", false, MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce)
        {
            Topic = null,
            DateTimeReceived = DateTime.UnixEpoch.AddSeconds(second)
        };
}

internal sealed class TopicTreeSelectionTestAdapter(TopicTreeStore store, IMessageStoreManager? observer = null) : ITopicSelectionStore
{
    private TopicTreeSnapshot? _snapshotToReturn;
    private Action? _beforeSnapshotRead;

    public TopicTreeStore Store { get; } = store;

    public Action? BeforeSnapshotRead
    {
        get => _beforeSnapshotRead;
        set => _beforeSnapshotRead = value;
    }

    public Action? BeforeClear { get; set; }

    public int ConditionalClearCalls { get; private set; }

    public List<TopicSelection?> Selections { get; } = [];

    public static TopicTreeSelectionTestAdapter Create(IMessageStoreManager? observer = null, int maxStoredMessages = 1000)
    {
        var configuration = new AppConfiguration
        {
            Performance = new PerformanceSettings { MaxStoredMessages = maxStoredMessages, MaxTopicNodes = 1000 }
        };
        var settings = Substitute.For<IPerformanceSettings>();
        settings.Performance.Returns(configuration.Performance);
        return new TopicTreeSelectionTestAdapter(new TopicTreeStore(settings, Substitute.For<ILogger>()), observer);
    }

    public TopicTreeSnapshot GetTopicTreeSnapshot()
    {
        if (Store.GetSelectedTopicState().Key is not null)
            Interlocked.Exchange(ref _beforeSnapshotRead, null)?.Invoke();

        if (_snapshotToReturn is not { } snapshot)
            return Store.GetTopicTreeSnapshot();

        _snapshotToReturn = null;
        return snapshot;
    }

    public SelectedTopicState GetSelectedTopicState() => Store.GetSelectedTopicState();

    public void SelectTopics(TopicSelection? selection)
    {
        Selections.Add(selection);
        observer?.SelectTopics(selection);
        Store.SelectTopics(selection);
    }

    public bool TryClearSelection(long treeVersion, SelectedTopicState expectedSelection)
    {
        ConditionalClearCalls++;
        BeforeClear?.Invoke();
        observer?.TryClearSelection(treeVersion, expectedSelection);
        return Store.TryClearSelection(treeVersion, expectedSelection);
    }

    public Task<SelectedMessagesSnapshot?> GetSelectedMessagesAsync(SelectedTopicToken expectedSelection, int limit) =>
        Task.FromResult(Store.GetSelectedMessages(expectedSelection, limit));

    public void Add(string topic, MqttMessage message) => Store.Add(topic, message);

    public void ReturnSnapshotOnce(TopicTreeSnapshot snapshot) => _snapshotToReturn = snapshot;
}
