using System.Collections.Immutable;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Mqtt;

namespace MqttProbe.UI.Tests.Components.Browser;

internal sealed class TopicBrowserTestFixture
{
    public TopicBrowserTestFixture(IMessageStoreManager? observer = null, int maxStoredMessages = 1000) =>
        Adapter = TopicTreeSelectionTestAdapter.Create(observer, maxStoredMessages);

    public TopicTreeSelectionTestAdapter Adapter { get; }

    public void Clear() => Adapter.Store.Clear();

    public void AddTopics(params (string Topic, int Messages)[] topics)
    {
        foreach (var (topic, count) in topics)
        {
            for (var index = 0; index < count; index++)
                Adapter.Add(topic, new MqttMessage($"payload-{index}", "unused", false,
                    MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce)
                { Topic = topic });
        }
    }

    // Runs just before the conditional clear is evaluated, so a test can move the store on
    // between the component's verdict and the clear without a race.
    public Action? BeforeConditionalClear
    {
        get => Adapter.BeforeClear;
        set => Adapter.BeforeClear = value;
    }

    public void SetSelection(TopicSelection? selection) => Adapter.Store.SelectTopics(selection);

    public string? SelectedKey => Adapter.Store.GetSelectedTopicState().Key;

}

internal static class TopicBrowserTestTrees
{
    public static (string Topic, int Messages)[] BuildTopics() =>
    [
        ("sensors/temp", 1),
        ("sensors/temp/room1", 2),
        ("sensors/humidity", 2)
    ];
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

    [Test]
    public void EveryProjectedRowCarriesItsBranchTarget()
    {
        var roots = ImmutableArray.Create(
            Node("a", "a", children:
            [
                Node("b", "a/b", messageCount: 2, hasDirectMessages: true)
            ]));

        var rows = TopicTreeProjection.CreateRows(
            roots, null, new HashSet<string> { "a" }, groupSparkplugTopics: false,
            selectionPath: null, out _, selectionMode: true);

        rows.Should().OnlyContain(row => row.Branch != null);
        rows.Should().ContainSingle(row => row.FullPath == "a/b")
            .Which.Branch.Path.Should().Be("a/b");
        rows.Should().NotContain(row => row.Kind == TopicTreeRowKind.Spelling);
    }

    private static TopicNodeSnapshot Node(
        string topic,
        string fullTopic,
        int topicCount = 1,
        int messageCount = 0,
        bool hasDirectMessages = false,
        ImmutableArray<string> observedPaths = default,
        params TopicNodeSnapshot[] children) =>
        new(topic, fullTopic, topicCount, messageCount, hasDirectMessages, children.ToImmutableArray())
        {
            ObservedTopicPaths = observedPaths.IsDefaultOrEmpty ? [] : observedPaths
        };
}
