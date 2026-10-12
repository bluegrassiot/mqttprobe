using System.Collections.Immutable;
using MqttProbe.Core.Models.Mqtt;

namespace MqttProbe.UI.Tests.Components.Browser;

// Core retention drops messages and leaves the topic nodes in place, so the tree can hold
// nodes with no messages of their own and no children. Grouping must keep them: only the
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

        // The valid tip groups; the zero-message residue keeps the verb prefix in the tree.
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
        // Every tip here is supported and moves, so nothing is left for the prefix rows.
        var tree = TopicSnapshotBuilder.Build(("spBv1.0/g/NDATA/n1", 2));

        var rows = Rows(tree);

        rows.Select(row => row.FullPath).Should().NotContain("spBv1.0/g/NDATA");
        TopicTreeProjection.RepresentsRealPath(tree, groupSparkplugTopics: true, "spBv1.0/g/NDATA")
            .Should().BeFalse();
        TopicTreeProjection.RepresentsRealPath(tree, groupSparkplugTopics: true, "spBv1.0/g/NDATA/n1")
            .Should().BeTrue("the grouped tip keeps the real topic");
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
