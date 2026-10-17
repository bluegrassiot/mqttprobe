using System.Collections.Immutable;
using MqttProbe.Core.Models.Mqtt;

namespace MqttProbe.UI.Tests.Components.Browser;

// Core retention leaves emptied topic nodes in place, so grouping must keep them: only a
// representation that actually moved into the grouped partition may disappear.
[TestFixture]
public sealed class TopicTreeRetentionProjectionTests
{
    private static HashSet<string> Expanded(ImmutableArray<TopicNodeSnapshot> roots)
    {
        var ids = new HashSet<string>();
        TopicTreeProjection.AddExpandablePaths(roots, groupSparkplugTopics: true, ids);
        return ids;
    }

    private static List<TopicTreeRowModel> Rows(ImmutableArray<TopicNodeSnapshot> roots) =>
        TopicTreeProjection.CreateRows(roots, null, Expanded(roots), groupSparkplugTopics: true);

    [Test]
    public void GroupingOn_KeepsARetainedZeroMessageStateTopic()
    {
        var tree = TopicSnapshotBuilder.Build(("spBv1.0/g/STATE/host", 0));

        var rows = Rows(tree);

        rows.Select(row => (row.FullPath, row.Depth, row.Kind)).Should().Equal(
            ("spBv1.0", 0, TopicTreeRowKind.SparkplugNamespace),
            ("spBv1.0/g", 1, TopicTreeRowKind.SparkplugGroup),
            ("spBv1.0/g/STATE", 2, TopicTreeRowKind.Topic),
            ("spBv1.0/g/STATE/host", 3, TopicTreeRowKind.Topic));
        rows.Should().OnlyContain(row => !row.IsVirtualTopic);
        rows.Single(row => row.FullPath == "spBv1.0/g/STATE/host").MessageCount.Should().Be(0);
        rows.Single(row => row.FullPath == "spBv1.0/g/STATE").TopicCount.Should().Be(1);

        TopicTreeProjection.RepresentsRealPath(tree, groupSparkplugTopics: true, "spBv1.0/g/STATE/host")
            .Should().BeTrue();
        TopicTreeProjection.RepresentsRealPath(tree, groupSparkplugTopics: true, "spBv1.0/g/STATE")
            .Should().BeTrue();
    }

    [Test]
    public void GroupingOn_KeepsRetainedZeroMessageUnknownVerbAndMalformedNodes()
    {
        var tree = TopicSnapshotBuilder.Build(
            ("spBv1.0/g/FOO/node9", 0),
            ("spBv1.0/g/NDATA/ghosttip/leaf", 0));

        var rows = Rows(tree);

        rows.Should().OnlyContain(row => !row.IsVirtualTopic);
        rows.Select(row => row.FullPath).Should().Equal(
            "spBv1.0",
            "spBv1.0/g",
            "spBv1.0/g/FOO",
            "spBv1.0/g/FOO/node9",
            "spBv1.0/g/NDATA",
            "spBv1.0/g/NDATA/ghosttip",
            "spBv1.0/g/NDATA/ghosttip/leaf");

        foreach (var path in new[]
                 {
                     "spBv1.0/g/FOO", "spBv1.0/g/FOO/node9",
                     "spBv1.0/g/NDATA", "spBv1.0/g/NDATA/ghosttip", "spBv1.0/g/NDATA/ghosttip/leaf"
                 })
        {
            TopicTreeProjection.RepresentsRealPath(tree, groupSparkplugTopics: true, path)
                .Should().BeTrue(path);
        }
    }

    [Test]
    public void GroupingOn_ZeroMessageResidueKeepsAPrefixWhoseOtherTipMoved()
    {
        var tree = TopicSnapshotBuilder.Build(
            ("spBv1.0/g/NDATA/n1", 2),
            ("spBv1.0/g/NDATA/ghosttip/leaf", 0));

        var rows = Rows(tree);

        rows.Should().Contain(row => row.DisplayName == "spBv1.0/g/NDATA/n1");
        rows.Select(row => row.FullPath).Should().Contain(
            "spBv1.0/g/NDATA",
            "spBv1.0/g/NDATA/ghosttip",
            "spBv1.0/g/NDATA/ghosttip/leaf");
        var prefix = rows.Single(row => row.FullPath == "spBv1.0/g/NDATA");
        prefix.IsVirtualTopic.Should().BeFalse();
        prefix.MessageCount.Should().Be(2, "the prefix keeps its own source total");
        TopicTreeProjection.RepresentsRealPath(tree, groupSparkplugTopics: true, "spBv1.0/g/NDATA")
            .Should().BeTrue();
    }

    [Test]
    public void GroupingOn_RemovesAPrefixWhoseWholeRepresentationMoved()
    {
        var tree = TopicSnapshotBuilder.Build(("spBv1.0/g/NDATA/n1", 2));

        var rows = Rows(tree);

        rows.Select(row => row.FullPath).Should().NotContain("spBv1.0/g/NDATA");
        TopicTreeProjection.RepresentsRealPath(tree, groupSparkplugTopics: true, "spBv1.0/g/NDATA")
            .Should().BeFalse();
        TopicTreeProjection.RepresentsRealPath(tree, groupSparkplugTopics: true, "spBv1.0/g/NDATA/n1")
            .Should().BeTrue("the grouped tip keeps the real topic");
    }

    [Test]
    public void GroupingOn_KeepsAChildlessRetainedNodeTipGrouped()
    {
        var tree = TopicSnapshotBuilder.Build(("spBv1.0/g/NDATA/n1", 0));

        var rows = Rows(tree);

        rows.Select(row => (row.DisplayName, row.Depth, row.Kind)).Should().Equal(
            ("spBv1.0", 0, TopicTreeRowKind.SparkplugNamespace),
            ("g", 1, TopicTreeRowKind.SparkplugGroup),
            ("n1", 2, TopicTreeRowKind.SparkplugNode),
            ("NDATA", 3, TopicTreeRowKind.MessageType),
            ("spBv1.0/g/NDATA/n1", 4, TopicTreeRowKind.Topic));

        var tip = rows[^1];
        tip.FullPath.Should().Be("spBv1.0/g/NDATA/n1");
        tip.IsVirtualTopic.Should().BeFalse();
        tip.IsValueBearer.Should().BeFalse("the store reports no direct message for it");
        tip.TopicCount.Should().Be(0);
        tip.MessageCount.Should().Be(0);

        var nodeRow = rows.Single(row => row.Kind == TopicTreeRowKind.SparkplugNode);
        nodeRow.TopicCount.Should().Be(1);
        nodeRow.MessageCount.Should().Be(0);

        rows.Select(row => row.FullPath).Should().NotContain("spBv1.0/g/NDATA",
            "nothing is left for a residual prefix");
        TopicTreeProjection.RepresentsRealPath(tree, groupSparkplugTopics: true, "spBv1.0/g/NDATA/n1")
            .Should().BeTrue();
    }

    [Test]
    public void GroupingOn_KeepsAChildlessRetainedDeviceTipGrouped()
    {
        var tree = TopicSnapshotBuilder.Build(("spBv1.0/g/DDATA/n1/dev1", 0));

        var rows = Rows(tree);

        rows.Select(row => (row.DisplayName, row.Depth, row.Kind)).Should().Equal(
            ("spBv1.0", 0, TopicTreeRowKind.SparkplugNamespace),
            ("g", 1, TopicTreeRowKind.SparkplugGroup),
            ("n1", 2, TopicTreeRowKind.SparkplugNode),
            ("dev1", 3, TopicTreeRowKind.SparkplugDevice),
            ("DDATA", 4, TopicTreeRowKind.MessageType),
            ("spBv1.0/g/DDATA/n1/dev1", 5, TopicTreeRowKind.Topic));

        rows[^1].IsValueBearer.Should().BeFalse();
        rows[^1].MessageCount.Should().Be(0);
        rows.Single(row => row.Kind == TopicTreeRowKind.SparkplugDevice).MessageCount.Should().Be(0);
        rows.Select(row => row.FullPath).Should().NotContain("spBv1.0/g/DDATA");
    }

    [TestCase("NBIRTH")]
    [TestCase("NDEATH")]
    [TestCase("NDATA")]
    [TestCase("NCMD")]
    public void GroupingOn_KeepsChildlessRetainedTipsForEveryNodeVerb(string verb)
    {
        var tree = TopicSnapshotBuilder.Build(($"spBv1.0/g/{verb}/n1", 0));

        var rows = Rows(tree);

        var nodeRow = rows.Single(row => row.Kind == TopicTreeRowKind.SparkplugNode);
        var typeRow = rows.Single(row => row.Kind == TopicTreeRowKind.MessageType && row.DisplayName == verb);
        typeRow.Depth.Should().Be(nodeRow.Depth + 1, "the message-type group sits directly beneath the node");
        rows.Should().Contain(row => row.FullPath == $"spBv1.0/g/{verb}/n1" && !row.IsVirtualTopic);
    }

    [TestCase("DBIRTH")]
    [TestCase("DDEATH")]
    [TestCase("DDATA")]
    [TestCase("DCMD")]
    public void GroupingOn_KeepsChildlessRetainedTipsForEveryDeviceVerb(string verb)
    {
        var tree = TopicSnapshotBuilder.Build(($"spBv1.0/g/{verb}/n1/dev1", 0));

        var rows = Rows(tree);

        rows.Should().Contain(row => row.Kind == TopicTreeRowKind.SparkplugDevice);
        rows.Should().Contain(row => row.Kind == TopicTreeRowKind.MessageType && row.DisplayName == verb);
        rows.Should().Contain(row => row.FullPath == $"spBv1.0/g/{verb}/n1/dev1" && !row.IsVirtualTopic);
    }

    [Test]
    public void GroupingOn_KeepsAChildlessRetainedTipBesideAMalformedPrefix()
    {
        var tree = TopicSnapshotBuilder.Build(
            ("spBv1.0/g/NDATA/n1", 0),
            ("spBv1.0/g/NDATA/ghosttip/leaf", 0));

        var rows = Rows(tree);

        rows.Should().Contain(row => row.FullPath == "spBv1.0/g/NDATA/n1" && !row.IsVirtualTopic);
        rows.Count(row => row.FullPath == "spBv1.0/g/NDATA/n1").Should().Be(1);
        var prefix = rows.Single(row => row.FullPath == "spBv1.0/g/NDATA");
        prefix.IsVirtualTopic.Should().BeFalse();
        prefix.MessageCount.Should().Be(0);
        rows.Select(row => row.FullPath).Should().Contain(
            "spBv1.0/g/NDATA/ghosttip",
            "spBv1.0/g/NDATA/ghosttip/leaf");
        Subtree(rows, "spBv1.0/g/NDATA").Select(row => row.FullPath)
            .Should().NotContain("spBv1.0/g/NDATA/n1");
    }

    [Test]
    public void GroupingOn_KeepsARetainedNodeTipGroupedWhileItsMalformedDescendantStays()
    {
        var tree = TopicSnapshotBuilder.Build(("spBv1.0/g/NDATA/n1", 0), ("spBv1.0/g/NDATA/n1/extra", 1));

        var rows = Rows(tree);

        rows.Select(row => (row.DisplayName, row.Depth, row.Kind)).Should().Equal(
            ("spBv1.0", 0, TopicTreeRowKind.SparkplugNamespace),
            ("g", 1, TopicTreeRowKind.SparkplugGroup),
            ("n1", 2, TopicTreeRowKind.SparkplugNode),
            ("NDATA", 3, TopicTreeRowKind.MessageType),
            ("spBv1.0/g/NDATA/n1", 4, TopicTreeRowKind.Topic),
            ("extra", 5, TopicTreeRowKind.Topic));

        var tip = rows.Single(row => row.FullPath == "spBv1.0/g/NDATA/n1");
        tip.IsVirtualTopic.Should().BeFalse();
        tip.IsValueBearer.Should().BeFalse("the store reports no direct message of its own");
        tip.TopicCount.Should().Be(1);
        tip.MessageCount.Should().Be(1, "the descendant's message still counts in the store totals");
        rows.Count(row => row.FullPath == "spBv1.0/g/NDATA/n1").Should().Be(1);
        rows.Select(row => row.FullPath).Should().NotContain("spBv1.0/g/NDATA",
            "nothing is left for a residual prefix");
        rows.Single(row => row.FullPath == "spBv1.0/g/NDATA/n1/extra").MessageCount.Should().Be(1);
    }

    [Test]
    public void GroupingOn_KeepsARetainedDeviceTipGroupedWhileItsMalformedDescendantStays()
    {
        var tree = TopicSnapshotBuilder.Build(
            ("spBv1.0/g/DDATA/n1/dev1", 0),
            ("spBv1.0/g/DDATA/n1/dev1/extra", 1));

        var rows = Rows(tree);

        rows.Select(row => (row.DisplayName, row.Depth, row.Kind)).Should().Equal(
            ("spBv1.0", 0, TopicTreeRowKind.SparkplugNamespace),
            ("g", 1, TopicTreeRowKind.SparkplugGroup),
            ("n1", 2, TopicTreeRowKind.SparkplugNode),
            ("dev1", 3, TopicTreeRowKind.SparkplugDevice),
            ("DDATA", 4, TopicTreeRowKind.MessageType),
            ("spBv1.0/g/DDATA/n1/dev1", 5, TopicTreeRowKind.Topic),
            ("extra", 6, TopicTreeRowKind.Topic));

        rows.Single(row => row.FullPath == "spBv1.0/g/DDATA/n1/dev1").IsValueBearer.Should().BeFalse();
        rows.Single(row => row.FullPath == "spBv1.0/g/DDATA/n1/dev1").MessageCount.Should().Be(1);
        rows.Select(row => row.FullPath).Should().NotContain("spBv1.0/g/DDATA");
    }

    [TestCase("NBIRTH")]
    [TestCase("NDEATH")]
    [TestCase("NDATA")]
    [TestCase("NCMD")]
    public void GroupingOn_KeepsRetainedNodeTipsWithDescendantsForEveryNodeVerb(string verb)
    {
        var tree = TopicSnapshotBuilder.Build(
            ($"spBv1.0/g/{verb}/n1", 0),
            ($"spBv1.0/g/{verb}/n1/extra", 1));

        var rows = Rows(tree);

        rows.Should().Contain(row => row.Kind == TopicTreeRowKind.MessageType && row.DisplayName == verb);
        rows.Should().Contain(row => row.FullPath == $"spBv1.0/g/{verb}/n1" && !row.IsVirtualTopic);
        rows.Should().Contain(row => row.FullPath == $"spBv1.0/g/{verb}/n1/extra");
    }

    [TestCase("DBIRTH")]
    [TestCase("DDEATH")]
    [TestCase("DDATA")]
    [TestCase("DCMD")]
    public void GroupingOn_KeepsRetainedDeviceTipsWithDescendantsForEveryDeviceVerb(string verb)
    {
        var tree = TopicSnapshotBuilder.Build(
            ($"spBv1.0/g/{verb}/n1/dev1", 0),
            ($"spBv1.0/g/{verb}/n1/dev1/extra", 1));

        var rows = Rows(tree);

        rows.Should().Contain(row => row.Kind == TopicTreeRowKind.MessageType && row.DisplayName == verb);
        rows.Should().Contain(row => row.FullPath == $"spBv1.0/g/{verb}/n1/dev1" && !row.IsVirtualTopic);
        rows.Should().Contain(row => row.FullPath == $"spBv1.0/g/{verb}/n1/dev1/extra");
    }

    [Test]
    public void GroupingOn_StructuralPrefixWithAMessageBearingDescendantStaysLiteral()
    {
        var tree = TopicSnapshotBuilder.Build(("spBv1.0/g/NDATA/ghostnode/ghostleaf", 1));

        var rows = Rows(tree);

        rows.Should().OnlyContain(row => !row.IsVirtualTopic);
        rows.Select(row => row.FullPath).Should().Equal(
            "spBv1.0",
            "spBv1.0/g",
            "spBv1.0/g/NDATA",
            "spBv1.0/g/NDATA/ghostnode",
            "spBv1.0/g/NDATA/ghostnode/ghostleaf");
    }

    [Test]
    public void GroupingOn_ClearedEndpointHistoryWithDescendantsReclassifiesTheTipAsLiteral()
    {
        var tree = TopicSnapshotBuilder.Build(
            ("spBv1.0/g/NDATA/n1", 0, false),
            ("spBv1.0/g/NDATA/n1/extra", 1, true));

        var rows = Rows(tree);

        rows.Should().OnlyContain(row => !row.IsVirtualTopic);
        rows.Select(row => row.FullPath).Should().Equal(
            "spBv1.0",
            "spBv1.0/g",
            "spBv1.0/g/NDATA",
            "spBv1.0/g/NDATA/n1",
            "spBv1.0/g/NDATA/n1/extra");

        TopicTreeProjection.RepresentsRealPath(tree, groupSparkplugTopics: true, "spBv1.0/g/NDATA/n1")
            .Should().BeTrue("the residual row still represents the path");
    }

    [Test]
    public void GroupingOn_ClearedEndpointThatKeepsADirectMessageStaysLiteralWithItsMessage()
    {
        // Deliberately inconsistent snapshot: the marker says unobserved while a direct message
        // survives, pinning the projection's treatment of the flag independently of the store.
        var tree = TopicSnapshotBuilder.Build(("spBv1.0/g/NDATA/n1", 2, false));

        var rows = Rows(tree);

        rows.Should().OnlyContain(row => !row.IsVirtualTopic);
        rows.Select(row => row.FullPath).Should().Equal(
            "spBv1.0",
            "spBv1.0/g",
            "spBv1.0/g/NDATA",
            "spBv1.0/g/NDATA/n1");

        var literal = rows.Single(row => row.FullPath == "spBv1.0/g/NDATA/n1");
        literal.IsValueBearer.Should().BeTrue("the store still holds its direct message");
        literal.MessageCount.Should().Be(2);
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
    public void GroupingOff_KeepsRetainedZeroMessageNodes()
    {
        var tree = TopicSnapshotBuilder.Build(("spBv1.0/g/STATE/host", 0));

        var rows = TopicTreeProjection.CreateRows(
            tree, null, Expanded(tree), groupSparkplugTopics: false);

        rows.Select(row => row.FullPath).Should().Equal("spBv1.0", "spBv1.0/g", "spBv1.0/g/STATE", "spBv1.0/g/STATE/host");
        rows.Should().OnlyContain(row => !row.IsVirtualTopic);
        TopicTreeProjection.RepresentsRealPath(tree, groupSparkplugTopics: false, "spBv1.0/g/STATE/host")
            .Should().BeTrue();
    }
}
