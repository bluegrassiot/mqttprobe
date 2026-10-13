using System.Collections.Immutable;
using MqttProbe.Core.Models.Mqtt;

namespace MqttProbe.UI.Tests.Components.Browser;

// A node or device tip groups only when a message was published at its own normalized path.
// "spBv1.0/g/NDATA//n1" lands on the same node as "spBv1.0/g/NDATA/n1" but was an alias
// spelling, so the canonical marker rather than the general observed one decides grouping.
[TestFixture]
public sealed class TopicTreeCanonicalTipEligibilityTests
{
    private static HashSet<string> Expanded(ImmutableArray<TopicNodeSnapshot> roots)
    {
        var ids = new HashSet<string>();
        TopicTreeProjection.AddExpandablePaths(roots, groupSparkplugTopics: true, ids);
        return ids;
    }

    private static List<TopicTreeRowModel> Rows(ImmutableArray<TopicNodeSnapshot> roots) =>
        TopicTreeProjection.CreateRows(roots, null, Expanded(roots), groupSparkplugTopics: true);

    private static TopicNodeSnapshot Node(ImmutableArray<TopicNodeSnapshot> roots, string fullPath)
    {
        foreach (var root in roots)
        {
            if (root.FullTopic == fullPath)
                return root;

            var found = Descendant(root, fullPath);
            if (found is not null)
                return found;
        }

        throw new InvalidOperationException(fullPath + " is not in the snapshot");
    }

    private static TopicNodeSnapshot? Descendant(TopicNodeSnapshot node, string fullPath)
    {
        foreach (var child in node.Children)
        {
            if (child.FullTopic == fullPath)
                return child;

            var found = Descendant(child, fullPath);
            if (found is not null)
                return found;
        }

        return null;
    }

    private static ImmutableArray<TopicNodeSnapshot> Replace(
        ImmutableArray<TopicNodeSnapshot> roots,
        string fullPath,
        TopicNodeSnapshot replacement)
    {
        var builder = ImmutableArray.CreateBuilder<TopicNodeSnapshot>(roots.Length);
        foreach (var root in roots)
            builder.Add(root.FullTopic == fullPath ? replacement : ReplaceChild(root, fullPath, replacement));

        return builder.ToImmutable();
    }

    private static TopicNodeSnapshot ReplaceChild(
        TopicNodeSnapshot node,
        string fullPath,
        TopicNodeSnapshot replacement)
    {
        if (node.FullTopic == fullPath)
            return replacement;

        if (Descendant(node, fullPath) is null)
            return node;

        return node with
        {
            Children = [.. node.Children.Select(child => ReplaceChild(child, fullPath, replacement))]
        };
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
    public void AliasSpellingOfANodeTipStaysLiteral()
    {
        var tree = TopicSnapshotBuilder.Build(("spBv1.0/g/NDATA//n1", 2, true, false));

        var rows = Rows(tree);

        rows.Should().OnlyContain(row => !row.IsVirtualTopic);
        rows.Select(row => (row.DisplayName, row.Depth, row.FullPath)).Should().Equal(
            ("spBv1.0", 0, "spBv1.0"),
            ("g", 1, "spBv1.0/g"),
            ("NDATA", 2, "spBv1.0/g/NDATA"),
            ("n1", 3, "spBv1.0/g/NDATA/n1"));

        var tip = rows[^1];
        tip.IsValueBearer.Should().BeTrue("the store does hold messages on the node");
        tip.MessageCount.Should().Be(2);
    }

    [Test]
    public void AliasSpellingOfADeviceTipStaysLiteral()
    {
        var tree = TopicSnapshotBuilder.Build(("spBv1.0/g/DDATA/n//dev1", 2, true, false));

        var rows = Rows(tree);

        rows.Should().OnlyContain(row => !row.IsVirtualTopic);
        rows.Select(row => row.FullPath).Should().Equal(
            "spBv1.0", "spBv1.0/g", "spBv1.0/g/DDATA", "spBv1.0/g/DDATA/n", "spBv1.0/g/DDATA/n/dev1");
    }

    [TestCase("spBv1.0/g/NDATA//n1")]
    [TestCase("spBv1.0/g/NDATA/n1//")]
    [TestCase("/spBv1.0/g/NDATA/n1")]
    [TestCase("spBv1.0/g/NDATA///n1")]
    public void LeadingTrailingAndRepeatedSeparatorsAllStayLiteral(string alias)
    {
        var tree = TopicSnapshotBuilder.Build((alias, 1, true, false));

        var rows = Rows(tree);

        rows.Should().OnlyContain(row => !row.IsVirtualTopic);
        rows[^1].FullPath.Should().Be("spBv1.0/g/NDATA/n1",
            "the alias still lands on the normalized node, shown on its own path");
        rows.Count(row => row.FullPath == "spBv1.0/g/NDATA/n1").Should().Be(1,
            "the alias is not presented as a row of its own");
    }

    [TestCase("NBIRTH")]
    [TestCase("NDEATH")]
    [TestCase("NDATA")]
    [TestCase("NCMD")]
    public void AliasSpellingStaysLiteralForEveryNodeVerb(string verb) =>
        AssertAliasStaysLiteral($"spBv1.0/g/{verb}//n1");

    [TestCase("DBIRTH")]
    [TestCase("DDEATH")]
    [TestCase("DDATA")]
    [TestCase("DCMD")]
    public void AliasSpellingStaysLiteralForEveryDeviceVerb(string verb) =>
        AssertAliasStaysLiteral($"spBv1.0/g/{verb}/n//dev1");

    private static void AssertAliasStaysLiteral(string alias)
    {
        var rows = Rows(TopicSnapshotBuilder.Build((alias, 1, true, false)));

        rows.Should().OnlyContain(row => !row.IsVirtualTopic, alias + " must not be regrouped");
    }

    [Test]
    public void CanonicalTipWithAMalformedDescendantStillGroups()
    {
        var tree = TopicSnapshotBuilder.Build(
            ("spBv1.0/g/NDATA/n1", 0, true, true),
            ("spBv1.0/g/NDATA/n1/extra", 1, true, true));

        var rows = Rows(tree);

        rows.Select(row => (row.DisplayName, row.Kind)).Should().Equal(
            ("spBv1.0", TopicTreeRowKind.SparkplugNamespace),
            ("g", TopicTreeRowKind.SparkplugGroup),
            ("n1", TopicTreeRowKind.SparkplugNode),
            ("NDATA", TopicTreeRowKind.MessageType),
            ("spBv1.0/g/NDATA/n1", TopicTreeRowKind.Topic),
            ("extra", TopicTreeRowKind.Topic));
        rows.Should().NotContain(row => row.FullPath == "spBv1.0/g/NDATA",
            "a fully grouped tip leaves no residual prefix");
    }

    [Test]
    public void CanonicalDeviceTipWithAMalformedDescendantStillGroups()
    {
        var tree = TopicSnapshotBuilder.Build(
            ("spBv1.0/g/DDATA/n/dev1", 0, true, true),
            ("spBv1.0/g/DDATA/n/dev1/extra", 1, true, true));

        var rows = Rows(tree);

        rows.Should().Contain(row => row.Kind == TopicTreeRowKind.SparkplugDevice && row.DisplayName == "dev1");
        var tip = rows.Single(row => row.FullPath == "spBv1.0/g/DDATA/n/dev1");
        tip.IsValueBearer.Should().BeFalse("retention emptied the tip itself");
        tip.MessageCount.Should().Be(1, "the descendant's message still counts in the store totals");
        rows.Single(row => row.FullPath == "spBv1.0/g/DDATA/n/dev1/extra").Depth.Should().Be(tip.Depth + 1);
    }

    [Test]
    public void ANodeObservedBothCanonicallyAndByAliasGroupsOnceAndKeepsTheStoreCounts()
    {
        var tree = TopicSnapshotBuilder.Build(
            ("spBv1.0/g/NDATA/n1", 2, true, true),
            ("spBv1.0/g/NDATA//n1", 3, true, false));

        var rows = Rows(tree);

        rows.Count(row => row.Kind == TopicTreeRowKind.SparkplugNode).Should().Be(1);
        rows.Count(row => row.FullPath == "spBv1.0/g/NDATA/n1").Should().Be(1,
            "the tip is presented once, never twice for the two spellings");
        rows.Should().NotContain(row => row.FullPath == "spBv1.0/g/NDATA",
            "the grouped tip leaves no residual prefix behind");

        var nodeRow = rows.Single(row => row.Kind == TopicTreeRowKind.SparkplugNode);
        nodeRow.DisplayName.Should().Be("n1");
        nodeRow.TopicCount.Should().Be(1);
        nodeRow.MessageCount.Should().Be(5, "both spellings are the store's messages on this node");

        var tip = rows.Single(row => row.FullPath == "spBv1.0/g/NDATA/n1");
        tip.DisplayName.Should().Be("spBv1.0/g/NDATA/n1", "a grouped leaf shows its real topic");
        tip.MessageCount.Should().Be(5);
        tip.IsValueBearer.Should().BeTrue();
    }

    [Test]
    public void RetiringTheCanonicalHistoryReclassifiesTheAliasNodeAsLiteral()
    {
        const string Tip = "spBv1.0/g/NDATA/n1";
        var tree = TopicSnapshotBuilder.Build(
            ("spBv1.0/g/NDATA/n1", 2, true, true),
            ("spBv1.0/g/NDATA//n1", 3, true, false));

        var tip = Node(tree, Tip);
        tip.HasObservedDirectTopic.Should().BeTrue("the alias history is still observed");
        tip.HasObservedCanonicalTopic.Should().BeTrue("the canonical history is still observed");

        var retired = Replace(tree, Tip, tip with { HasObservedCanonicalTopic = false });
        var retiredTip = Node(retired, Tip);
        retiredTip.HasObservedDirectTopic.Should().BeTrue("retiring the canonical part keeps the general marker");

        var rows = Rows(retired);

        rows.Should().OnlyContain(row => !row.IsVirtualTopic);
        rows.Select(row => row.FullPath).Should().Equal(
            "spBv1.0", "spBv1.0/g", "spBv1.0/g/NDATA", Tip);
    }

    [Test]
    public void RetiringTheCanonicalHistoryKeepsTheResidualTipVisible()
    {
        const string Tip = "spBv1.0/g/NDATA/n1";
        var tree = TopicSnapshotBuilder.Build(
            ("spBv1.0/g/NDATA/n1", 0, true, true),
            ("spBv1.0/g/NDATA//n1", 0, true, false));

        Node(tree, Tip).HasObservedCanonicalTopic.Should().BeTrue(
            "both spellings were published at this node");

        var tip = Node(tree, Tip);
        var rows = Rows(Replace(tree, Tip, tip with { HasObservedCanonicalTopic = false }));

        rows.Select(row => row.FullPath).Should().Equal("spBv1.0", "spBv1.0/g", "spBv1.0/g/NDATA", Tip);
        rows[^1].IsValueBearer.Should().BeFalse("retention emptied the tip itself");
        rows.Should().OnlyContain(row => !row.IsVirtualTopic);
    }
}
