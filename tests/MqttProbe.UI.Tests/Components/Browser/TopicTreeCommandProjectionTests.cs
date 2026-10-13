using System.Collections.Immutable;
using MqttProbe.Core.Models.Mqtt;

namespace MqttProbe.UI.Tests.Components.Browser;

[TestFixture]
public sealed class TopicTreeCommandProjectionTests
{
    private static HashSet<string> Expanded(ImmutableArray<TopicNodeSnapshot> roots, bool group)
    {
        var ids = new HashSet<string>();
        TopicTreeProjection.AddExpandablePaths(roots, group, ids);
        return ids;
    }

    private static List<TopicTreeRowModel> Rows(ImmutableArray<TopicNodeSnapshot> roots, bool group = true) =>
        TopicTreeProjection.CreateRows(roots, null, Expanded(roots, group), group);

    [Test]
    public void GroupingOn_ProjectsNodeCommandWithItsRealTopicAndCount()
    {
        var tree = TopicSnapshotBuilder.Build(("spBv1.0/plant1/NCMD/node1", 2));

        var rows = Rows(tree);

        rows.Select(row => (row.DisplayName, row.Depth, row.Kind)).Should().Equal(
            ("spBv1.0", 0, TopicTreeRowKind.SparkplugNamespace),
            ("plant1", 1, TopicTreeRowKind.SparkplugGroup),
            ("node1", 2, TopicTreeRowKind.SparkplugNode),
            ("NCMD", 3, TopicTreeRowKind.MessageType),
            ("spBv1.0/plant1/NCMD/node1", 4, TopicTreeRowKind.Topic));

        var command = rows[^1];
        command.FullPath.Should().Be("spBv1.0/plant1/NCMD/node1");
        command.IsVirtualTopic.Should().BeFalse("a command topic is a real, selectable topic");
        command.IsValueBearer.Should().BeTrue();
        command.TopicCount.Should().Be(0);
        command.MessageCount.Should().Be(2);
        rows[0].MessageCount.Should().Be(2);
    }

    [Test]
    public void GroupingOn_ProjectsDeviceCommandWithItsRealTopicAndCount()
    {
        var tree = TopicSnapshotBuilder.Build(("spBv1.0/plant1/DCMD/node1/dev1", 3));

        var rows = Rows(tree);

        rows.Select(row => (row.DisplayName, row.Depth, row.Kind)).Should().Equal(
            ("spBv1.0", 0, TopicTreeRowKind.SparkplugNamespace),
            ("plant1", 1, TopicTreeRowKind.SparkplugGroup),
            ("node1", 2, TopicTreeRowKind.SparkplugNode),
            ("Devices", 3, TopicTreeRowKind.Devices),
            ("dev1", 4, TopicTreeRowKind.SparkplugDevice),
            ("DCMD", 5, TopicTreeRowKind.MessageType),
            ("spBv1.0/plant1/DCMD/node1/dev1", 6, TopicTreeRowKind.Topic));

        rows[^1].IsVirtualTopic.Should().BeFalse();
        rows[^1].MessageCount.Should().Be(3);
        rows.Single(row => row.Kind == TopicTreeRowKind.SparkplugDevice).MessageCount.Should().Be(3);
    }

    [Test]
    public void GroupingOn_OrdersCommandsAfterBirthDeathAndData()
    {
        var tree = TopicSnapshotBuilder.Build(
            ("spBv1.0/g/NCMD/n", 1),
            ("spBv1.0/g/NDATA/n", 1),
            ("spBv1.0/g/NDEATH/n", 1),
            ("spBv1.0/g/NBIRTH/n", 1),
            ("spBv1.0/g/DCMD/n/d", 1),
            ("spBv1.0/g/DDATA/n/d", 1),
            ("spBv1.0/g/DDEATH/n/d", 1),
            ("spBv1.0/g/DBIRTH/n/d", 1));

        var rows = Rows(tree);

        rows.Where(row => row.Kind == TopicTreeRowKind.MessageType).Select(row => row.DisplayName)
            .Should().Equal("NBIRTH", "NDEATH", "NDATA", "NCMD", "DBIRTH", "DDEATH", "DDATA", "DCMD");
    }

    [Test]
    public void GroupingOn_CommandSharesTheNodeBranchWithItsDataMessages()
    {
        var tree = TopicSnapshotBuilder.Build(
            ("spBv1.0/g/NCMD/n", 1),
            ("spBv1.0/g/NDATA/n", 2));

        var rows = Rows(tree);

        rows.Count(row => row.Kind == TopicTreeRowKind.SparkplugNode).Should().Be(1);
        rows.Where(row => row.Kind == TopicTreeRowKind.MessageType).Select(row => row.DisplayName)
            .Should().Equal("NDATA", "NCMD");

        var nodeRow = rows.Single(row => row.Kind == TopicTreeRowKind.SparkplugNode);
        rows.Count(row => row.Kind == TopicTreeRowKind.MessageType && row.Depth == nodeRow.Depth + 1)
            .Should().Be(2, "both message-type groups sit directly beneath the node");
        nodeRow.TopicCount.Should().Be(2);
        nodeRow.MessageCount.Should().Be(3);
    }

    [Test]
    public void GroupingOn_MalformedCommandsStayLiteral()
    {
        var tree = TopicSnapshotBuilder.Build(
            ("spBv1.0/g/NCMD/n/extra", 1),
            ("spBv1.0/g2/NCMD", 1),
            ("spBv1.0/g3/DCMD/n", 1),
            ("spBv1.0/g4/DCMD/n/dev/extra", 1));

        var rows = Rows(tree);

        rows.Should().OnlyContain(row => !row.IsVirtualTopic);
        rows.Select(row => row.DisplayName).Should().Equal(
            "spBv1.0", "g", "NCMD", "n", "extra", "g2", "NCMD",
            "g3", "DCMD", "n", "g4", "DCMD", "n", "dev", "extra");
        rows.Select(row => row.FullPath).Should().Contain(
            "spBv1.0/g/NCMD/n/extra",
            "spBv1.0/g2/NCMD",
            "spBv1.0/g3/DCMD/n",
            "spBv1.0/g4/DCMD/n/dev/extra");
    }

    [Test]
    public void GroupingOff_CommandsStayInTheLiteralHierarchy()
    {
        var tree = TopicSnapshotBuilder.Build(
            ("spBv1.0/plant1/NCMD/node1", 2),
            ("spBv1.0/plant1/DCMD/node1/dev1", 3));

        var rows = Rows(tree, group: false);

        rows.Should().OnlyContain(row => row.Kind == TopicTreeRowKind.Topic);
        rows.Select(row => (row.FullPath, row.Depth)).Should().Equal(
            ("spBv1.0", 0),
            ("spBv1.0/plant1", 1),
            ("spBv1.0/plant1/DCMD", 2),
            ("spBv1.0/plant1/DCMD/node1", 3),
            ("spBv1.0/plant1/DCMD/node1/dev1", 4),
            ("spBv1.0/plant1/NCMD", 2),
            ("spBv1.0/plant1/NCMD/node1", 3));
        rows[0].TopicCount.Should().Be(6);
        rows[0].MessageCount.Should().Be(5);
    }
}
