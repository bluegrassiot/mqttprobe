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
        _manager.SelectTopic(Arg.Any<string?>()).Returns(call => Select(call.Arg<string?>()));
    }

    public void SetSnapshot(params TopicNodeSnapshot[] roots) =>
        _snapshot = new TopicTreeSnapshot(_snapshot.Version + 1, ImmutableArray.CreateRange(roots));

    public void SetSelection(SelectedTopicState selection) => _selection = selection;

    private TopicSelectionResult Select(string? fullTopic)
    {
        if (fullTopic is null)
        {
            _selection = new SelectedTopicState(new SelectedTopicToken(_storeId, ++_generation, 0), null, 0);
            return new TopicSelectionResult(TopicSelectionStatus.Cleared, _selection);
        }

        var node = Find(_snapshot.Roots, fullTopic);
        if (node is null)
            return new TopicSelectionResult(TopicSelectionStatus.NotFound, _selection);
        if (_selection.FullTopic != fullTopic)
            _selection = new SelectedTopicState(new SelectedTopicToken(_storeId, ++_generation, 0), fullTopic, node.MessageCount);
        return new TopicSelectionResult(TopicSelectionStatus.Selected, _selection);
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

        var rows = TopicTreeProjection.CreateRows(roots, null, new HashSet<string> { "root2", "root2/child2" });

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

        var rows = TopicTreeProjection.CreateRows(roots, "room1", expanded);

        rows.Select(row => row.FullPath)
            .Should().Equal("sensors", "sensors/temperature", "sensors/temperature/room1");
        rows[0].IsExpanded.Should().BeTrue();
        rows[1].IsExpanded.Should().BeTrue();
        expanded.Should().ContainSingle().Which.Should().Be("sensors/humidity");

        var restoredRows = TopicTreeProjection.CreateRows(roots, null, expanded);
        restoredRows.Select(row => row.FullPath).Should().Equal("sensors");
    }

    [Test]
    public void CreateRows_CollapsedBranchHidesChildrenWithoutFilter()
    {
        var rows = TopicTreeProjection.CreateRows(
            [Node("root", "root", children: [Node("child", "root/child")])],
            null,
            new HashSet<string>());

        rows.Select(row => row.FullPath).Should().Equal("root");
        rows[0].HasChildren.Should().BeTrue();
        rows[0].IsExpanded.Should().BeFalse();
    }

    [Test]
    public void CreateRows_DefaultRootsReturnsNoRows() =>
        TopicTreeProjection.CreateRows(default, null, new HashSet<string>()).Should().BeEmpty();

    private static TopicNodeSnapshot Node(
        string topic,
        string fullTopic,
        int topicCount = 1,
        int messageCount = 0,
        bool hasDirectMessages = false,
        params TopicNodeSnapshot[] children) =>
        new(topic, fullTopic, topicCount, messageCount, hasDirectMessages, children.ToImmutableArray());
}
