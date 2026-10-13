using System.Collections.Immutable;
using MqttProbe.Core.Models.Mqtt;

namespace MqttProbe.UI.Tests.Components.Browser;

[TestFixture]
public sealed class TopicTreePartitionProjectionTests
{
    private static HashSet<string> Expanded(ImmutableArray<TopicNodeSnapshot> roots)
    {
        var ids = new HashSet<string>();
        TopicTreeProjection.AddExpandablePaths(roots, groupSparkplugTopics: true, ids);
        return ids;
    }

    private static List<TopicTreeRowModel> Rows(ImmutableArray<TopicNodeSnapshot> roots) =>
        TopicTreeProjection.CreateRows(roots, null, Expanded(roots), groupSparkplugTopics: true);

    private static TopicNodeSnapshot Snapshot(ImmutableArray<TopicNodeSnapshot> roots, string fullPath)
    {
        foreach (var root in roots)
        {
            var found = Find(root);
            if (found is not null)
                return found;
        }

        throw new InvalidOperationException(fullPath + " is not in the snapshot");

        TopicNodeSnapshot? Find(TopicNodeSnapshot node)
        {
            if (node.FullTopic == fullPath)
                return node;

            foreach (var child in node.Children)
            {
                var match = Find(child);
                if (match is not null)
                    return match;
            }

            return null;
        }
    }

    private static List<TopicTreeRowModel> Subtree(List<TopicTreeRowModel> rows, string fullPath)
    {
        var index = rows.FindIndex(row => row.FullPath == fullPath);
        index.Should().BeGreaterThanOrEqualTo(0, fullPath + " is present");

        var root = rows[index];
        var subtree = new List<TopicTreeRowModel>();
        for (var i = index + 1; i < rows.Count && rows[i].Depth > root.Depth; i++)
            subtree.Add(rows[i]);

        return subtree;
    }

    [Test]
    public void GroupingOn_MalformedMessageOnTheVerbLevelDoesNotStopValidDescendantsGrouping()
    {
        var tree = TopicSnapshotBuilder.Build(
            ("spBv1.0/g/NDATA", 1),
            ("spBv1.0/g/NDATA/n1", 2));

        var rows = Rows(tree);

        rows.Select(row => (row.DisplayName, row.Depth, row.Kind)).Should().Equal(
            ("spBv1.0", 0, TopicTreeRowKind.SparkplugNamespace),
            ("g", 1, TopicTreeRowKind.SparkplugGroup),
            ("n1", 2, TopicTreeRowKind.SparkplugNode),
            ("Node messages", 3, TopicTreeRowKind.NodeMessages),
            ("NDATA", 4, TopicTreeRowKind.MessageType),
            ("spBv1.0/g/NDATA/n1", 5, TopicTreeRowKind.Topic),
            ("NDATA", 2, TopicTreeRowKind.Topic));

        var residual = rows.Single(row => row.FullPath == "spBv1.0/g/NDATA");
        residual.TopicCount.Should().Be(1);
        residual.MessageCount.Should().Be(3);
        Subtree(rows, "spBv1.0/g/NDATA").Should().BeEmpty("the tip moved into the grouped partition");
    }

    [Test]
    public void GroupingOn_MalformedMessageOnTheDeviceLevelDoesNotStopValidDescendantsGrouping()
    {
        var tree = TopicSnapshotBuilder.Build(
            ("spBv1.0/g/DDATA/n", 1),
            ("spBv1.0/g/DDATA/n/dev1", 2));

        var rows = Rows(tree);

        rows.Select(row => (row.DisplayName, row.Depth, row.Kind)).Should().Equal(
            ("spBv1.0", 0, TopicTreeRowKind.SparkplugNamespace),
            ("g", 1, TopicTreeRowKind.SparkplugGroup),
            ("DDATA", 2, TopicTreeRowKind.Topic),
            ("n", 3, TopicTreeRowKind.Topic),
            ("n", 2, TopicTreeRowKind.SparkplugNode),
            ("Devices", 3, TopicTreeRowKind.Devices),
            ("dev1", 4, TopicTreeRowKind.SparkplugDevice),
            ("DDATA", 5, TopicTreeRowKind.MessageType),
            ("spBv1.0/g/DDATA/n/dev1", 6, TopicTreeRowKind.Topic));

        rows.Single(row => row.FullPath == "spBv1.0/g/DDATA").MessageCount.Should().Be(3);
        rows.Single(row => row.FullPath == "spBv1.0/g/DDATA/n")
            .MessageCount.Should().Be(3, "the prefix keeps its own source totals");
        Subtree(rows, "spBv1.0/g/DDATA/n")
            .Select(row => row.FullPath).Should().NotContain("spBv1.0/g/DDATA/n/dev1");
    }

    [Test]
    public void GroupingOn_PartitionsCommandsPerTip()
    {
        var tree = TopicSnapshotBuilder.Build(
            ("spBv1.0/g/NCMD/n1", 2),
            ("spBv1.0/g/NCMD/n2/extra", 1));

        var rows = Rows(tree);

        rows.Should().Contain(row => row.DisplayName == "spBv1.0/g/NCMD/n1");
        rows.Should().Contain(row => row.FullPath == "spBv1.0/g/NCMD/n2/extra");
        rows.Single(row => row.FullPath == "spBv1.0/g/NCMD").MessageCount.Should().Be(3);
        rows.Count(row => row.FullPath == "spBv1.0/g/NCMD/n1").Should().Be(1);
        Subtree(rows, "spBv1.0/g/NCMD").Select(row => row.FullPath).Should().Equal(
            "spBv1.0/g/NCMD/n2", "spBv1.0/g/NCMD/n2/extra");
    }

    [Test]
    public void GroupingOn_EveryRowKeepsOneIdentityAndOnePlace()
    {
        var tree = TopicSnapshotBuilder.Build(
            ("spBv1.0/g/NDATA/n1", 2),
            ("spBv1.0/g/NDATA/n2/extra", 1),
            ("spBv1.0/g/DDATA/n1/dev1", 2),
            ("spBv1.0/g/DDATA/n1/dev2/extra", 1),
            ("spBv1.0/g/NCMD/n1", 1),
            ("spBv1.0/STATE/host", 1),
            ("industrial/machine/temp", 1));

        var rows = Rows(tree);

        rows.Select(row => row.FullPath).Should().OnlyHaveUniqueItems();
        rows.Where(row => row.IsVirtualTopic).Select(row => row.FullPath)
            .Should().OnlyContain(path => TopicTreeProjection.IsVirtualExpansionId(path));
        rows.Where(row => !row.IsVirtualTopic).Select(row => row.FullPath)
            .Should().OnlyContain(path => IsInSnapshot(tree, path));
        rows.Count(row => row.FullPath == "spBv1.0/g/NDATA/n1").Should().Be(1);
        rows.Count(row => row.FullPath == "spBv1.0/g/DDATA/n1/dev1").Should().Be(1);
    }

    [Test]
    public void GroupingOn_RealRowsKeepSourceTotalsWhileVirtualRowsCountOwnedTipsOnce()
    {
        var tree = TopicSnapshotBuilder.Build(
            ("spBv1.0/g/NDATA/n1", 2),
            ("spBv1.0/g/NDATA/n2/extra", 1),
            ("spBv1.0/g/DDATA/n1/dev1", 3),
            ("spBv1.0/g/DDATA/n1/dev2/extra", 1));

        var rows = Rows(tree);

        foreach (var path in new[]
                 {
                     "spBv1.0", "spBv1.0/g", "spBv1.0/g/NDATA", "spBv1.0/g/NDATA/n2",
                     "spBv1.0/g/DDATA", "spBv1.0/g/DDATA/n1"
                 })
        {
            var source = Snapshot(tree, path);
            var row = rows.Single(candidate => candidate.FullPath == path);
            row.TopicCount.Should().Be(source.TopicCount, path);
            row.MessageCount.Should().Be(source.MessageCount, path);
        }

        var nodeRow = rows.Single(row => row.Kind == TopicTreeRowKind.SparkplugNode);
        var ownedTips = new[]
        {
            Snapshot(tree, "spBv1.0/g/NDATA/n1"),
            Snapshot(tree, "spBv1.0/g/DDATA/n1/dev1")
        };
        nodeRow.TopicCount.Should().Be(ownedTips.Length);
        nodeRow.MessageCount.Should().Be(ownedTips.Sum(tip => tip.MessageCount));

        var nodeMessages = rows.Single(row => row.Kind == TopicTreeRowKind.NodeMessages);
        nodeMessages.MessageCount.Should().Be(Snapshot(tree, "spBv1.0/g/NDATA/n1").MessageCount);
        var devices = rows.Single(row => row.Kind == TopicTreeRowKind.Devices);
        devices.MessageCount.Should().Be(Snapshot(tree, "spBv1.0/g/DDATA/n1/dev1").MessageCount);
        devices.MessageCount.Should().NotBe(Snapshot(tree, "spBv1.0/g/DDATA").MessageCount,
            "the residual prefix total is not used for a virtual partition");
    }

    [Test]
    public void RepresentsRealPath_KeepsRetainedFallbackAggregatesAndDropsFullyGroupedOnes()
    {
        var mixed = TopicSnapshotBuilder.Build(
            ("spBv1.0/g/NDATA/n1", 2),
            ("spBv1.0/g/NDATA/n2/extra", 1),
            ("spBv1.0/g/DDATA/n1/dev1", 2),
            ("spBv1.0/g/DDATA/n1/dev2/extra", 1));

        TopicTreeProjection.RepresentsRealPath(mixed, groupSparkplugTopics: true, "spBv1.0/g/NDATA")
            .Should().BeTrue();
        TopicTreeProjection.RepresentsRealPath(mixed, groupSparkplugTopics: true, "spBv1.0/g/NDATA/n2")
            .Should().BeTrue();
        TopicTreeProjection.RepresentsRealPath(mixed, groupSparkplugTopics: true, "spBv1.0/g/DDATA")
            .Should().BeTrue();
        TopicTreeProjection.RepresentsRealPath(mixed, groupSparkplugTopics: true, "spBv1.0/g/DDATA/n1")
            .Should().BeTrue();

        var fullyGrouped = TopicSnapshotBuilder.Build(
            ("spBv1.0/g/NDATA/n1", 2),
            ("spBv1.0/g/DDATA/n1/dev1", 2));

        TopicTreeProjection.RepresentsRealPath(fullyGrouped, groupSparkplugTopics: true, "spBv1.0/g/NDATA")
            .Should().BeFalse();
        TopicTreeProjection.RepresentsRealPath(fullyGrouped, groupSparkplugTopics: true, "spBv1.0/g/DDATA/n1")
            .Should().BeFalse();
        TopicTreeProjection.RepresentsRealPath(fullyGrouped, groupSparkplugTopics: true, "spBv1.0/g/DDATA")
            .Should().BeFalse();
    }

    private static bool IsInSnapshot(ImmutableArray<TopicNodeSnapshot> roots, string fullPath)
    {
        foreach (var root in roots)
        {
            if (Contains(root))
                return true;
        }

        return false;

        bool Contains(TopicNodeSnapshot node) =>
            node.FullTopic == fullPath || node.Children.Any(Contains);
    }
}
