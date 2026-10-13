using System.Collections.Immutable;
using MqttProbe.Core.Models.Mqtt;

namespace MqttProbe.UI.Tests.Components.Browser;

internal sealed class TopicBrowserTestFixture
{
    private readonly MqttProbe.Core.Services.Mqtt.IMessageStoreManager _manager;
    private readonly Guid _storeId = Guid.NewGuid();
    private long _generation;
    private TopicTreeSnapshot _snapshot = new(0, ImmutableArray<TopicNodeSnapshot>.Empty);
    private SelectedTopicState _selection = new(default, null, 0);

    public TopicBrowserTestFixture(MqttProbe.Core.Services.Mqtt.IMessageStoreManager manager) => _manager = manager;

    public void Configure()
    {
        _manager.RootTopicCount.Returns(_ => _snapshot.Roots.IsDefault ? 0 : _snapshot.Roots.Length);
        _manager.GetTopicTreeSnapshot().Returns(_ => _snapshot);
        _manager.GetSelectedTopicState().Returns(_ => _selection);
        _manager.When(m => m.SelectTopic(Arg.Any<string?>())).Do(call => Select(call.Arg<string?>()));
        _manager.TryClearSelection(Arg.Any<long>(), Arg.Any<SelectedTopicState>())
            .Returns(call => TryClearSelection(call.Arg<long>(), call.Arg<SelectedTopicState>()));
    }

    public void SetSnapshot(params TopicNodeSnapshot[] roots) =>
        _snapshot = new TopicTreeSnapshot(_snapshot.Version + 1, ImmutableArray.CreateRange(roots));

    public TopicTreeSnapshot Snapshot => _snapshot;

    // Empties the store but keeps the reported version, so the component keeps the frame it
    // already rendered: the row is on screen while the topic it names has left the store.
    public void PurgeHoldingRenderedFrame() =>
        _snapshot = new TopicTreeSnapshot(_snapshot.Version, ImmutableArray<TopicNodeSnapshot>.Empty);

    // Runs just before the conditional clear is evaluated, so a test can move the store on
    // between the component's verdict and the clear without a race.
    public Action? BeforeConditionalClear { get; set; }

    // Mirrors TopicTreeStore.TryClearSelection so the component never meets a substitute's
    // default false, and a stale verdict comes from the store rather than from the mock.
    public bool TryClearSelection(long treeVersion, SelectedTopicState expectedSelection)
    {
        BeforeConditionalClear?.Invoke();

        if (_snapshot.Version != treeVersion
            || expectedSelection.Token.StoreId != _selection.Token.StoreId
            || expectedSelection.Token.Generation != _selection.Token.Generation
            || _selection.FullTopic is null
            || !string.Equals(_selection.FullTopic, expectedSelection.FullTopic, StringComparison.Ordinal))
            return false;

        _generation = Math.Max(_generation, _selection.Token.Generation);
        _selection = new SelectedTopicState(new SelectedTopicToken(_storeId, ++_generation, 0), null, 0);
        return true;
    }

    public void SetSelection(SelectedTopicState selection) => _selection = selection;

    public string? SelectedFullTopic => _selection.FullTopic;

    private void Select(string? fullTopic)
    {
        if (fullTopic is null)
        {
            _selection = new SelectedTopicState(new SelectedTopicToken(_storeId, ++_generation, 0), null, 0);
            return;
        }

        var node = Find(_snapshot.Roots, fullTopic);
        if (node is null)
            return;
        if (_selection.FullTopic != fullTopic)
            _selection = new SelectedTopicState(new SelectedTopicToken(_storeId, ++_generation, 0), fullTopic, node.MessageCount);
    }

    private static TopicNodeSnapshot? Find(ImmutableArray<TopicNodeSnapshot> nodes, string fullTopic)
    {
        if (nodes.IsDefaultOrEmpty)
            return null;
        foreach (var node in nodes)
        {
            if (node.FullTopic == fullTopic)
                return node;
            var match = Find(node.Children, fullTopic);
            if (match is not null)
                return match;
        }
        return null;
    }
}

internal static class TopicBrowserTestTrees
{
    public static TopicNodeSnapshot BuildTree() =>
        new("sensors", "sensors", 3, 5, false,
        [
            new TopicNodeSnapshot("temp", "sensors/temp", 1, 3, true,
            [new TopicNodeSnapshot("room1", "sensors/temp/room1", 0, 2, true, [])]),
            new TopicNodeSnapshot("humidity", "sensors/humidity", 0, 2, true, [])
        ]);

    public static TopicNodeSnapshot Node(string topic, int messageCount = 0, bool hasDirectMessages = false) =>
        new(topic, topic, 0, messageCount, hasDirectMessages, []);
}

[TestFixture]
public sealed class TopicTreeProjectionTests
{
    [Test]
    public void CreateRows_NaturallySortsRootsAndChildrenAndRetainsSnapshotMetadata()
    {
        var roots = ImmutableArray.Create(
            Node("root10", "root10", topicCount: 3, messageCount: 4),
            Node("root2", "root2", topicCount: 5, messageCount: 7, hasDirectMessages: true,
                children:
            [
                Node("child10", "root2/child10"),
                Node("child2", "root2/child2", hasDirectMessages: true, children:
                [
                    Node("leaf10", "root2/child2/leaf10"),
                    Node("leaf2", "root2/child2/leaf2")
                ])
            ]));

        var rows = TopicTreeProjection.CreateRows(
            roots, null, new HashSet<string> { "root2", "root2/child2" }, groupSparkplugTopics: false);

        rows.Select(row => row.FullPath).Should().Equal(
            "root2", "root2/child2", "root2/child2/leaf2", "root2/child2/leaf10", "root2/child10", "root10");
        rows[0].Depth.Should().Be(0);
        rows[0].TopicCount.Should().Be(5);
        rows[0].MessageCount.Should().Be(7);
        rows[0].IsValueBearer.Should().BeTrue();
        rows[0].HasChildren.Should().BeTrue();
        rows[1].IsValueBearer.Should().BeTrue();
    }

    [Test]
    public void CreateRows_FilterRetainsMatchingDescendantAncestorsAndDoesNotChangeExpansion()
    {
        var roots = ImmutableArray.Create(
            Node("sensors", "sensors", children:
            [
                Node("temperature", "sensors/temperature", children:
                [Node("room1", "sensors/temperature/room1")]),
                Node("humidity", "sensors/humidity")
            ]));
        var expanded = new HashSet<string> { "sensors/humidity" };

        var rows = TopicTreeProjection.CreateRows(roots, "room1", expanded, groupSparkplugTopics: false);

        rows.Select(row => row.FullPath)
            .Should().Equal("sensors", "sensors/temperature", "sensors/temperature/room1");
        rows[0].IsExpanded.Should().BeTrue();
        rows[1].IsExpanded.Should().BeTrue();
        expanded.Should().ContainSingle().Which.Should().Be("sensors/humidity");

        var restoredRows = TopicTreeProjection.CreateRows(roots, null, expanded, groupSparkplugTopics: false);
        restoredRows.Select(row => row.FullPath).Should().Equal("sensors");
    }

    [Test]
    public void CreateRows_CollapsedBranchHidesChildrenWithoutFilter()
    {
        var rows = TopicTreeProjection.CreateRows(
            [Node("root", "root", children: [Node("child", "root/child")])],
            null,
            new HashSet<string>(),
            groupSparkplugTopics: false);

        rows.Select(row => row.FullPath).Should().Equal("root");
        rows[0].HasChildren.Should().BeTrue();
        rows[0].IsExpanded.Should().BeFalse();
    }

    [Test]
    public void CreateRows_DefaultRootsReturnsNoRows() =>
        TopicTreeProjection.CreateRows(default, null, new HashSet<string>(), groupSparkplugTopics: false)
            .Should().BeEmpty();

    [Test]
    public void CreateRows_GroupingOff_OnACollapsedSnapshotDoesNotWalkHiddenDescendants()
    {
        // 200 roots of 20 leaves. A collapsed tree only needs its root rows, so cloning the
        // store into projection nodes first would allocate for all 4,200 nodes every refresh.
        var topics = new List<(string Topic, int Messages)>();
        for (var root = 0; root < 200; root++)
        {
            for (var leaf = 0; leaf < 20; leaf++)
                topics.Add(($"root{root}/leaf{leaf}", 1));
        }

        var tree = TopicSnapshotBuilder.Build([.. topics]);
        var collapsed = new HashSet<string>();
        TopicTreeProjection.CreateRows(tree, null, collapsed, groupSparkplugTopics: false)
            .Should().HaveCount(200);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var rows = TopicTreeProjection.CreateRows(tree, null, collapsed, groupSparkplugTopics: false);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        rows.Should().HaveCount(200);
        rows.Should().OnlyContain(row => !row.HasChildren || !row.IsExpanded);
        // Measured here: the direct traversal allocates about 19 KB against about 495 KB for
        // cloning the store first, so the bound sits between them.
        allocated.Should().BeLessThan(128 * 1024,
            "a collapsed literal tree must not be cloned node by node");
    }

    private static TopicNodeSnapshot Node(
        string topic,
        string fullTopic,
        int topicCount = 1,
        int messageCount = 0,
        bool hasDirectMessages = false,
        params TopicNodeSnapshot[] children) =>
        new(topic, fullTopic, topicCount, messageCount, hasDirectMessages, children.ToImmutableArray());
}
