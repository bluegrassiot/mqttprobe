using System.Collections.Immutable;
using MqttProbe.Core.Models.Mqtt;

namespace MqttProbe.UI.Tests.Components.Browser;

[TestFixture]
public sealed class TopicTreeGroupingProjectionTests
{
    private static ImmutableArray<TopicNodeSnapshot> SupportedTree() => TopicSnapshotBuilder.Build(
        ("spBv1.0/plant1/NDATA/node1", 3),
        ("spBv1.0/plant1/NBIRTH/node1", 1),
        ("spBv1.0/plant1/DDATA/node1/dev1", 4));

    private static ImmutableArray<TopicNodeSnapshot> MixedTree() => TopicSnapshotBuilder.Build(
        ("spBv1.0/plant1/NDATA/node1", 3),
        ("spBv1.0/plant1/DDATA/node1/dev1", 4),
        ("spBv1.0/plant1/STATE/scada", 1),
        ("spBv1.0/plant1/FOO/node9", 1),
        ("spBv1.0/plant1/NDATA/node1/extra", 1),
        ("spBv1.0/plant2/NDATA/node7", 5),
        ("spBv1.0/STATE/scada-host", 1),
        ("spBv2.0/plant9/NDATA/node1", 1),
        ("sensors/temp", 2));

    private static HashSet<string> Expanded(ImmutableArray<TopicNodeSnapshot> roots, bool group)
    {
        var ids = new HashSet<string>();
        TopicTreeProjection.AddExpandablePaths(roots, group, ids);
        return ids;
    }

    private static List<TopicTreeRowModel> Rows(
        ImmutableArray<TopicNodeSnapshot> roots,
        string? filter = null,
        HashSet<string>? expanded = null,
        bool group = true) =>
        TopicTreeProjection.CreateRows(roots, filter, expanded ?? Expanded(roots, group), group);

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

    private static TopicTreeRowModel Row(List<TopicTreeRowModel> rows, string displayName)
    {
        var row = rows.Find(candidate => candidate.DisplayName == displayName);
        row.Should().NotBeNull(displayName + " is present");
        return row!;
    }

    [Test]
    public void EveryRowKind_CarriesItsBranchTarget()
    {
        var rows = Rows(SupportedTree());

        Row(rows, "node1").Branch.Scope.Should().Be(
            new SparkplugSelectionScope(SparkplugSelectionKind.Node, "spBv1.0/plant1", "node1"));
        Row(rows, "dev1").Branch.Scope.Should().Be(
            new SparkplugSelectionScope(SparkplugSelectionKind.Device, "spBv1.0/plant1", "node1", "dev1"));
        Row(rows, "DDATA").Branch.MessageType.Should().Be("DDATA");
        Row(rows, "plant1").Branch.Kind.Should().Be(TopicBranchKind.Raw);
        Row(rows, "plant1").Branch.Path.Should().Be("spBv1.0/plant1");
        Row(rows, "spBv1.0").Branch.Path.Should().Be("spBv1.0");
    }

    [Test]
    public void BranchLabel_NamesTheRowScope()
    {
        var rows = Rows(SupportedTree());

        Row(rows, "NDATA").BranchLabel.Should().Be("spBv1.0/plant1/node1/NDATA");
        Row(rows, "node1").BranchLabel.Should().Be("spBv1.0/plant1/node1");
        Row(rows, "dev1").BranchLabel.Should().Be("spBv1.0/plant1/node1/dev1");
    }

    [Test]
    public void GroupedRows_CarryTheScopeThatResolvesThemAgainstTheStore()
    {
        var rows = Rows(SupportedTree());

        Row(rows, "node1").Branch.Scope.Should().Be(
            new SparkplugSelectionScope(SparkplugSelectionKind.Node, "spBv1.0/plant1", "node1"));
        Row(rows, "dev1").Branch.Scope.Should().Be(
            new SparkplugSelectionScope(SparkplugSelectionKind.Device, "spBv1.0/plant1", "node1", "dev1"));
        Row(rows, "NDATA").Branch.Scope.Should().Be(
            new SparkplugSelectionScope(SparkplugSelectionKind.Node, "spBv1.0/plant1", "node1"),
            "a node message type narrows its node scope by the verb");
        Row(rows, "NDATA").Branch.MessageType.Should().Be("NDATA");

        Row(rows, "plant1").Branch.Scope.Should().BeNull();
        Row(rows, "spBv1.0").Branch.Scope.Should().BeNull();
    }

    [Test]
    public void OnlyMessageTypeRows_CarryAMessageType()
    {
        var rows = Rows(SupportedTree());

        rows.Where(row => row.Kind == TopicTreeRowKind.MessageType)
            .Should().OnlyContain(row => row.Branch.MessageType != null);
        rows.Where(row => row.Kind != TopicTreeRowKind.MessageType)
            .Should().OnlyContain(row => row.Branch.MessageType == null);
    }

    [Test]
    public void ALiteralRowSelectsItsOwnTopic()
    {
        var rows = Rows(MixedTree(), group: false);

        var prefix = rows.Find(row => row.FullPath == "spBv1.0/plant1/NDATA");
        prefix.Should().NotBeNull();
        prefix!.Branch.Path.Should().Be("spBv1.0/plant1/NDATA");
        prefix.BranchLabel.Should().Be("spBv1.0/plant1/NDATA");

        var leaf = rows.Find(row => row.FullPath == "spBv1.0/plant1/NDATA/node1");
        leaf!.Branch.Path.Should().Be("spBv1.0/plant1/NDATA/node1");
    }

    [Test]
    public void GroupingOn_ProjectsSupportedTopicsByNodeAndDevice()
    {
        var rows = Rows(SupportedTree());

        rows.Select(row => (row.DisplayName, row.Depth, row.Kind)).Should().Equal(
            ("spBv1.0", 0, TopicTreeRowKind.SparkplugNamespace),
            ("plant1", 1, TopicTreeRowKind.SparkplugGroup),
            ("node1", 2, TopicTreeRowKind.SparkplugNode),
            ("NBIRTH", 3, TopicTreeRowKind.MessageType),
            ("spBv1.0/plant1/NBIRTH/node1", 4, TopicTreeRowKind.Topic),
            ("NDATA", 3, TopicTreeRowKind.MessageType),
            ("spBv1.0/plant1/NDATA/node1", 4, TopicTreeRowKind.Topic),
            ("dev1", 3, TopicTreeRowKind.SparkplugDevice),
            ("DDATA", 4, TopicTreeRowKind.MessageType),
            ("spBv1.0/plant1/DDATA/node1/dev1", 5, TopicTreeRowKind.Topic));
    }

    [Test]
    public void GroupingOn_LeafRowsCarryTheRealTopicAndOpenItForSelection()
    {
        var rows = Rows(SupportedTree());

        var leaves = rows.Where(row => !row.IsVirtualTopic && row.FullPath.StartsWith("spBv1.0/plant1/", StringComparison.Ordinal)).ToList();

        leaves.Select(row => row.FullPath).Should().Equal(
            "spBv1.0/plant1/NBIRTH/node1",
            "spBv1.0/plant1/NDATA/node1",
            "spBv1.0/plant1/DDATA/node1/dev1");
        leaves.Should().OnlyContain(leaf => leaf.DisplayName == leaf.FullPath);
        leaves.Should().OnlyContain(leaf => leaf.IsValueBearer);
        leaves.Should().OnlyContain(leaf => leaf.TopicCount == 0);
    }

    [Test]
    public void GroupingOn_OnlyGroupingRowsAreVirtual()
    {
        var rows = Rows(SupportedTree());

        rows.Where(row => row.IsVirtualTopic).Select(row => row.Kind).Distinct()
            .Should().BeEquivalentTo(new List<TopicTreeRowKind>
            {
                TopicTreeRowKind.SparkplugNode,
                TopicTreeRowKind.SparkplugDevice,
                TopicTreeRowKind.MessageType
            });

        rows.Where(row => row.DisplayName is "spBv1.0" or "plant1")
            .Should().OnlyContain(row => !row.IsVirtualTopic);
    }

    [Test]
    public void GroupingOn_VirtualIdsNeverCollideWithRealTopicPaths()
    {
        var tree = MixedTree();
        var rows = Rows(tree);

        var realPaths = RealTopicPaths(tree);
        var virtualPaths = rows.Where(row => row.IsVirtualTopic).Select(row => row.FullPath).ToList();

        virtualPaths.Should().OnlyContain(id => TopicTreeProjection.IsVirtualExpansionId(id));
        virtualPaths.Should().NotIntersectWith(realPaths);
        virtualPaths.Should().OnlyHaveUniqueItems();
        rows.Where(row => !row.IsVirtualTopic).Select(row => row.FullPath)
            .Should().OnlyContain(path => realPaths.Contains(path));
    }

    [Test]
    public void GroupingOn_AggregatesCountsWithoutDuplication()
    {
        var rows = Rows(SupportedTree());

        var namespaceRow = rows[0];
        var groupRow = rows.Single(row => row.Kind == TopicTreeRowKind.SparkplugGroup);
        var nodeRow = rows.Single(row => row.Kind == TopicTreeRowKind.SparkplugNode);
        var device = rows.Single(row => row.Kind == TopicTreeRowKind.SparkplugDevice);

        namespaceRow.TopicCount.Should().Be(8);
        namespaceRow.MessageCount.Should().Be(8);
        groupRow.TopicCount.Should().Be(7);
        groupRow.MessageCount.Should().Be(8);

        nodeRow.TopicCount.Should().Be(3);
        nodeRow.MessageCount.Should().Be(8);
        device.TopicCount.Should().Be(1);
        device.MessageCount.Should().Be(4);
        device.Depth.Should().Be(nodeRow.Depth + 1,
            "a device is a direct child of the node, beside the node's own message types");

        var nodeTypes = rows.Where(row => row.Kind == TopicTreeRowKind.MessageType
            && row.Depth == nodeRow.Depth + 1).ToList();
        nodeTypes.Select(row => row.DisplayName).Should().Equal("NBIRTH", "NDATA");
        nodeTypes.Sum(row => row.TopicCount).Should().Be(2);
        nodeTypes.Sum(row => row.MessageCount).Should().Be(4);
        nodeRow.MessageCount.Should().Be(nodeTypes.Sum(row => row.MessageCount) + device.MessageCount);
    }

    [Test]
    public void GroupingOn_OrdersMessageTypesBirthDeathData()
    {
        var tree = TopicSnapshotBuilder.Build(
            ("spBv1.0/g/NDATA/n", 1),
            ("spBv1.0/g/NBIRTH/n", 1),
            ("spBv1.0/g/NDEATH/n", 1));

        var rows = Rows(tree);

        rows.Where(row => row.Kind == TopicTreeRowKind.MessageType).Select(row => row.DisplayName)
            .Should().Equal("NBIRTH", "NDEATH", "NDATA");
    }

    [Test]
    public void GroupingOn_KeepsMultipleGroupsNodesAndDevicesDistinct()
    {
        var tree = TopicSnapshotBuilder.Build(
            ("spBv1.0/plant1/NDATA/node1", 1),
            ("spBv1.0/plant1/NDATA/node2", 1),
            ("spBv1.0/plant1/DDATA/node1/dev1", 1),
            ("spBv1.0/plant1/DDATA/node1/dev2", 1),
            ("spBv1.0/plant2/NDATA/node1", 1));

        var rows = Rows(tree);

        rows.Where(row => row.Kind == TopicTreeRowKind.SparkplugGroup).Select(row => row.DisplayName)
            .Should().Equal("plant1", "plant2");
        rows.Where(row => row.Kind == TopicTreeRowKind.SparkplugNode).Select(row => row.DisplayName)
            .Should().Equal("node1", "node2", "node1");
        rows.Where(row => row.Kind == TopicTreeRowKind.SparkplugDevice).Select(row => row.DisplayName)
            .Should().Equal("dev1", "dev2");
        rows.Where(row => !row.IsVirtualTopic && row.DisplayName.StartsWith("spBv1.0/", StringComparison.Ordinal))
            .Select(row => row.FullPath).Should().Equal(
                "spBv1.0/plant1/NDATA/node1",
                "spBv1.0/plant1/DDATA/node1/dev1",
                "spBv1.0/plant1/DDATA/node1/dev2",
                "spBv1.0/plant1/NDATA/node2",
                "spBv1.0/plant2/NDATA/node1");
    }

    [Test]
    public void GroupingOn_KeepsUnsupportedAndMalformedTopicsInTheLiteralHierarchy()
    {
        var rows = Rows(MixedTree());

        rows.Select(row => (row.DisplayName, row.Depth)).Should().Contain(new (string, int)[]
        {
            ("STATE", 2),
            ("scada", 3),
            ("FOO", 2),
            ("node9", 3),
            ("extra", 5),
            ("STATE", 1),
            ("scada-host", 2),
            ("spBv2.0", 0),             // a different namespace is never regrouped
            ("plant9", 1),
            ("sensors", 0)
        });

        rows.Where(row => row.IsVirtualTopic).Select(row => row.DisplayName)
            .Should().NotContain("scada").And.NotContain("node9").And.NotContain("extra");
        rows.Where(row => !row.IsVirtualTopic && row.DisplayName == "scada")
            .Should().OnlyContain(row => row.FullPath == "spBv1.0/plant1/STATE/scada");
        rows.Where(row => !row.IsVirtualTopic && row.DisplayName == "extra")
            .Should().OnlyContain(row => row.FullPath == "spBv1.0/plant1/NDATA/node1/extra");
    }

    [Test]
    public void GroupingOn_MalformedChildStaysUnderItsRealTopicRow()
    {
        var rows = Rows(MixedTree());

        var malformedTip = rows.Single(row => row.DisplayName == "spBv1.0/plant1/NDATA/node1");
        var malformedChild = rows.Single(row => row.FullPath == "spBv1.0/plant1/NDATA/node1/extra");

        malformedTip.HasChildren.Should().BeTrue();
        malformedChild.Depth.Should().Be(malformedTip.Depth + 1);
        malformedChild.MessageCount.Should().Be(1);
    }

    [Test]
    public void GroupingOn_StandaloneMalformedNodePathStaysLiteral()
    {
        var tree = TopicSnapshotBuilder.Build(("spBv1.0/plant1/NDATA/ghost/extra", 2));

        var rows = Rows(tree);

        rows.Should().OnlyContain(row => !row.IsVirtualTopic);
        rows.Select(row => (row.DisplayName, row.Depth, row.FullPath)).Should().Equal(
            ("spBv1.0", 0, "spBv1.0"),
            ("plant1", 1, "spBv1.0/plant1"),
            ("NDATA", 2, "spBv1.0/plant1/NDATA"),
            ("ghost", 3, "spBv1.0/plant1/NDATA/ghost"),
            ("extra", 4, "spBv1.0/plant1/NDATA/ghost/extra"));
        rows[0].TopicCount.Should().Be(4);
        rows[0].MessageCount.Should().Be(2);
        rows[^1].MessageCount.Should().Be(2);
    }

    [Test]
    public void GroupingOn_StandaloneMalformedDevicePathStaysLiteral()
    {
        var tree = TopicSnapshotBuilder.Build(("spBv1.0/plant1/DDATA/ghost/dev/x", 3));

        var rows = Rows(tree);

        rows.Should().OnlyContain(row => !row.IsVirtualTopic);
        rows.Select(row => (row.DisplayName, row.Depth)).Should().Equal(
            ("spBv1.0", 0),
            ("plant1", 1),
            ("DDATA", 2),
            ("ghost", 3),
            ("dev", 4),
            ("x", 5));
        rows.Select(row => row.MessageCount).Should().Equal(3, 3, 3, 3, 3, 3);
    }

    [Test]
    public void GroupingOn_MalformedNodeTipStaysLiteralBesideAValidGroupedTip()
    {
        var tree = TopicSnapshotBuilder.Build(
            ("spBv1.0/g/NDATA/n1", 2),
            ("spBv1.0/g/NDATA/n2/extra", 1));

        var rows = Rows(tree);

        rows.Select(row => (row.DisplayName, row.Depth, row.Kind)).Should().Equal(
            ("spBv1.0", 0, TopicTreeRowKind.SparkplugNamespace),
            ("g", 1, TopicTreeRowKind.SparkplugGroup),
            ("n1", 2, TopicTreeRowKind.SparkplugNode),
            ("NDATA", 3, TopicTreeRowKind.MessageType),
            ("spBv1.0/g/NDATA/n1", 4, TopicTreeRowKind.Topic),
            ("NDATA", 2, TopicTreeRowKind.Topic),
            ("n2", 3, TopicTreeRowKind.Topic),
            ("extra", 4, TopicTreeRowKind.Topic));

        var residual = rows.Single(row => row.FullPath == "spBv1.0/g/NDATA");
        residual.IsVirtualTopic.Should().BeFalse();
        residual.TopicCount.Should().Be(3);
        residual.MessageCount.Should().Be(3);

        rows.Count(row => row.FullPath == "spBv1.0/g/NDATA/n1").Should().Be(1);
        Subtree(rows, "spBv1.0/g/NDATA").Select(row => row.FullPath)
            .Should().NotContain("spBv1.0/g/NDATA/n1");
    }

    [Test]
    public void GroupingOn_MalformedDeviceTipStaysLiteralBesideAValidGroupedTip()
    {
        var tree = TopicSnapshotBuilder.Build(
            ("spBv1.0/g/DDATA/n/dev1", 2),
            ("spBv1.0/g/DDATA/n/dev2/extra", 1),
            ("spBv1.0/g/NDATA/n", 4));

        var rows = Rows(tree);

        var residualPrefix = rows.Single(row => row.FullPath == "spBv1.0/g/DDATA");
        var residualNode = rows.Single(row => row.FullPath == "spBv1.0/g/DDATA/n");
        rows.Select(row => row.FullPath).Should().Contain(
            "spBv1.0/g/DDATA/n/dev2",
            "spBv1.0/g/DDATA/n/dev2/extra");
        rows.Count(row => row.FullPath == "spBv1.0/g/DDATA/n/dev1").Should().Be(1);
        Subtree(rows, "spBv1.0/g/DDATA/n").Select(row => row.FullPath)
            .Should().NotContain("spBv1.0/g/DDATA/n/dev1");

        residualPrefix.IsVirtualTopic.Should().BeFalse();
        residualPrefix.TopicCount.Should().Be(4);
        residualPrefix.MessageCount.Should().Be(3);
        residualNode.TopicCount.Should().Be(3);

        var nodeRow = rows.Single(row => row.Kind == TopicTreeRowKind.SparkplugNode);
        nodeRow.DisplayName.Should().Be("n");
        nodeRow.TopicCount.Should().Be(2);
        nodeRow.MessageCount.Should().Be(6);
        rows.Should().Contain(row => row.DisplayName == "spBv1.0/g/NDATA/n");
        rows.Should().Contain(row => row.DisplayName == "spBv1.0/g/DDATA/n/dev1");
    }

    [Test]
    public void GroupingOn_DeviceVerbWithNodeLevelMessagesStaysLiteral()
    {
        var tree = TopicSnapshotBuilder.Build(("spBv1.0/g/DDATA/n", 2));

        var rows = Rows(tree);

        rows.Should().OnlyContain(row => !row.IsVirtualTopic);
        rows.Select(row => row.DisplayName).Should().Equal("spBv1.0", "g", "DDATA", "n");
    }

    [Test]
    public void GroupingOn_EmptyNodeIdStaysALiteralEmptyLevel()
    {
        var rows = Rows(TopicSnapshotBuilder.Build(("spBv1.0/g/NDATA/", 1)));

        rows.Should().OnlyContain(row => !row.IsVirtualTopic);
        rows.Select(row => (row.DisplayName, row.Depth)).Should().Equal(
            ("spBv1.0", 0), ("g", 1), ("NDATA", 2), ("", 3));
    }

    [Test]
    public void GroupingOn_EmptyDeviceIdStaysALiteralEmptyLevel()
    {
        var rows = Rows(TopicSnapshotBuilder.Build(("spBv1.0/g/DDATA/n/", 1)));

        rows.Should().OnlyContain(row => !row.IsVirtualTopic);
        rows.Select(row => (row.DisplayName, row.Depth)).Should().Equal(
            ("spBv1.0", 0), ("g", 1), ("DDATA", 2), ("n", 3), ("", 4));
    }

    [Test]
    public void GroupingOn_EmptyGroupIdStaysALiteralEmptyLevel()
    {
        var rows = Rows(TopicSnapshotBuilder.Build(("spBv1.0//NDATA/n", 1)));

        rows.Should().OnlyContain(row => !row.IsVirtualTopic);
        rows.Select(row => (row.DisplayName, row.Depth)).Should().Equal(
            ("spBv1.0", 0), ("", 1), ("NDATA", 2), ("n", 3));
    }

    [Test]
    public void GroupingOn_DeviceTipWithMalformedDescendantStaysGrouped()
    {
        var tree = TopicSnapshotBuilder.Build(
            ("spBv1.0/g/DDATA/n/dev", 2),
            ("spBv1.0/g/DDATA/n/dev/extra", 1));

        var rows = Rows(tree);

        var tip = rows.Single(row => row.FullPath == "spBv1.0/g/DDATA/n/dev");
        tip.IsVirtualTopic.Should().BeFalse();
        tip.DisplayName.Should().Be("spBv1.0/g/DDATA/n/dev");
        tip.MessageCount.Should().Be(3);
        rows.Single(row => row.FullPath == "spBv1.0/g/DDATA/n/dev/extra").Depth.Should().Be(tip.Depth + 1);
        rows.Should().Contain(row => row.Kind == TopicTreeRowKind.SparkplugDevice);
    }

    [Test]
    public void GroupingOn_FilterMatchesTheDisplayedTopicOfAGroupedLeaf()
    {
        var rows = Rows(MixedTree(), "spBv1.0/plant1/NDATA/node1");

        rows.Select(row => (row.DisplayName, row.Depth)).Should().Equal(
            ("spBv1.0", 0),
            ("plant1", 1),
            ("node1", 2),
            ("NDATA", 3),
            ("spBv1.0/plant1/NDATA/node1", 4));
    }

    [Test]
    public void GroupingOn_FilterMatchesAMultiSegmentPathFragment()
    {
        var rows = Rows(MixedTree(), "plant1/NDATA");

        rows.Select(row => (row.DisplayName, row.Depth)).Should().Equal(
            ("spBv1.0", 0),
            ("plant1", 1),
            ("node1", 2),
            ("NDATA", 3),
            ("spBv1.0/plant1/NDATA/node1", 4));
    }

    [Test]
    public void GroupingOn_FilterMatchesTheDisplayedDeviceTopic()
    {
        var rows = Rows(MixedTree(), "DDATA/node1/dev1");

        rows.Select(row => row.DisplayName).Should().Equal(
            "spBv1.0", "plant1", "node1", "dev1", "DDATA", "spBv1.0/plant1/DDATA/node1/dev1");
    }

    [Test]
    public void GroupingOff_FullTopicPathFilterStillMatchesNothing()
    {
        var tree = SupportedTree();

        var rows = TopicTreeProjection.CreateRows(
            tree, "spBv1.0/plant1/NDATA/node1", Expanded(tree, group: false), groupSparkplugTopics: false);

        rows.Should().BeEmpty();
    }

    [Test]
    public void GroupingOff_SegmentFilterIsUnchanged()
    {
        var tree = SupportedTree();

        var rows = TopicTreeProjection.CreateRows(
            tree, "node1", Expanded(tree, group: false), groupSparkplugTopics: false);

        rows.Select(row => row.FullPath).Should().Equal(
            "spBv1.0",
            "spBv1.0/plant1",
            "spBv1.0/plant1/DDATA",
            "spBv1.0/plant1/DDATA/node1",
            "spBv1.0/plant1/NBIRTH",
            "spBv1.0/plant1/NBIRTH/node1",
            "spBv1.0/plant1/NDATA",
            "spBv1.0/plant1/NDATA/node1");
    }

    [Test]
    public void GroupingOn_CollapsedShowsOnlyRoots()
    {
        var rows = TopicTreeProjection.CreateRows(SupportedTree(), null, new HashSet<string>(), groupSparkplugTopics: true);

        rows.Select(row => row.DisplayName).Should().Equal("spBv1.0");
        rows[0].HasChildren.Should().BeTrue();
        rows[0].IsExpanded.Should().BeFalse();
    }

    [Test]
    public void GroupingOn_FilterMatchesRealSegmentsAndRetainsAncestors()
    {
        var rows = Rows(MixedTree(), "temp");

        rows.Select(row => row.DisplayName).Should().Equal("sensors", "temp");
        rows.Should().OnlyContain(row => !row.IsVirtualTopic);
    }

    [Test]
    public void GroupingOn_FilterRetainsGroupedAncestorsForMatchingRealTopic()
    {
        var rows = Rows(MixedTree(), "node7");

        rows.Select(row => row.DisplayName).Should().Equal(
            "spBv1.0", "plant2", "node7", "NDATA", "spBv1.0/plant2/NDATA/node7");
        rows.Should().OnlyContain(row => row.IsExpanded);
    }

    [Test]
    public void GroupingOn_FilterNoLongerMatchesARemovedDevicesWrapper()
    {
        var rows = Rows(MixedTree(), "Devices");

        rows.Should().BeEmpty("the synthetic Devices wrapper is gone, so nothing carries that label");
    }

    [Test]
    public void GroupingOn_FilterMatchesLiteralFallbackSubtrees()
    {
        var rows = Rows(MixedTree(), "scada");

        rows.Select(row => row.DisplayName).Should().Equal(
            "spBv1.0", "plant1", "STATE", "scada", "STATE", "scada-host");
    }

    [Test]
    public void GroupingOn_FilterWithNoMatchReturnsNoRows()
    {
        var rows = Rows(MixedTree(), "nothing-matches-this");

        rows.Should().BeEmpty();
    }

    [Test]
    public void GroupingOff_KeepsTheLiteralHierarchyUnchanged()
    {
        var tree = SupportedTree();

        var rows = Rows(tree, group: false);

        rows.Should().OnlyContain(row => row.Kind == TopicTreeRowKind.Topic);
        rows.Should().OnlyContain(row => !row.IsVirtualTopic);
        rows.Select(row => (row.FullPath, row.Depth)).Should().Equal(
            ("spBv1.0", 0),
            ("spBv1.0/plant1", 1),
            ("spBv1.0/plant1/DDATA", 2),
            ("spBv1.0/plant1/DDATA/node1", 3),
            ("spBv1.0/plant1/DDATA/node1/dev1", 4),
            ("spBv1.0/plant1/NBIRTH", 2),
            ("spBv1.0/plant1/NBIRTH/node1", 3),
            ("spBv1.0/plant1/NDATA", 2),
            ("spBv1.0/plant1/NDATA/node1", 3));
        rows.Should().OnlyContain(row => row.DisplayName == LastSegment(row.FullPath));
    }

    [Test]
    public void GroupingOff_FilterAndCountsMatchTheLiteralProjection()
    {
        var tree = SupportedTree();

        var rows = Rows(tree, "dev1", group: false);

        rows.Select(row => row.FullPath).Should().Equal(
            "spBv1.0", "spBv1.0/plant1", "spBv1.0/plant1/DDATA", "spBv1.0/plant1/DDATA/node1", "spBv1.0/plant1/DDATA/node1/dev1");
        rows[0].TopicCount.Should().Be(8);
        rows[0].MessageCount.Should().Be(8);
        rows[^1].TopicCount.Should().Be(0);
        rows[^1].MessageCount.Should().Be(4);
    }

    [Test]
    public void AddExpandablePaths_IncludesGroupingRowsWhenOn()
    {
        var onIds = new HashSet<string>();
        TopicTreeProjection.AddExpandablePaths(SupportedTree(), groupSparkplugTopics: true, onIds);

        var rows = TopicTreeProjection.CreateRows(
            SupportedTree(), null, onIds, groupSparkplugTopics: true);

        onIds.Should().Contain(id => TopicTreeProjection.IsVirtualExpansionId(id));
        rows.Should().HaveCount(10);
        rows.Should().OnlyContain(row => !row.HasChildren || row.IsExpanded);
    }

    [Test]
    public void AddExpandablePaths_ExcludesGroupingRowsWhenOff()
    {
        var ids = new HashSet<string>();
        TopicTreeProjection.AddExpandablePaths(SupportedTree(), groupSparkplugTopics: false, ids);

        ids.Should().OnlyContain(id => !TopicTreeProjection.IsVirtualExpansionId(id));
        ids.Should().BeEquivalentTo("spBv1.0", "spBv1.0/plant1", "spBv1.0/plant1/DDATA", "spBv1.0/plant1/DDATA/node1",
            "spBv1.0/plant1/NBIRTH", "spBv1.0/plant1/NDATA");
    }

    private static string LastSegment(string topic)
    {
        var index = topic.LastIndexOf('/');
        return index < 0 ? topic : topic[(index + 1)..];
    }

    private static HashSet<string> RealTopicPaths(ImmutableArray<TopicNodeSnapshot> nodes)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes)
            Collect(node);
        return paths;

        void Collect(TopicNodeSnapshot node)
        {
            paths.Add(node.FullTopic);
            foreach (var child in node.Children)
                Collect(child);
        }
    }
}
