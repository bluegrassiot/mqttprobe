using MqttProbe.Core.Models.Sparkplug;

namespace MqttProbe.UI.Tests.Components.Sparkplug;

[TestFixture]
public sealed class SparkplugMetricHierarchyTests
{
    private static SpbMetricSnapshot Metric(string name, string value = "1", string dataType = "double") =>
        new(name, dataType, value, DateTime.UtcNow);

    [Test]
    public void Build_SplitsMetricNamesAndKeepsTheExactLeafName()
    {
        var roots = SparkplugMetricHierarchy.Build(
            [Metric("Vancouver/Area1/Line1/Zone1/Asset1/T1", "23.4")]);

        var vancouver = roots.Should().ContainSingle().Which;
        vancouver.Label.Should().Be("Vancouver");
        vancouver.Depth.Should().Be(0);
        vancouver.Metric.Should().BeNull();

        var area = vancouver.Children.Should().ContainSingle().Which;
        area.Label.Should().Be("Area1");
        area.Depth.Should().Be(1);

        var leaf = Descend(area);
        leaf.Label.Should().Be("T1");
        leaf.Depth.Should().Be(5);
        leaf.FullPath.Should().Be("Vancouver/Area1/Line1/Zone1/Asset1/T1");
        leaf.Metric!.Value.Should().Be("23.4");
    }

    [Test]
    public void Build_KeepsAMetricAndItsDescendants()
    {
        var roots = SparkplugMetricHierarchy.Build([Metric("A", "10"), Metric("A/B", "20")]);

        var a = roots.Should().ContainSingle().Which;
        a.Metric!.Name.Should().Be("A");
        a.Metric.Value.Should().Be("10");
        a.Children.Should().ContainSingle().Which.Label.Should().Be("B");
        a.Children[0].Metric!.Name.Should().Be("A/B");
        a.MetricCount.Should().Be(2);
    }

    [Test]
    public void Build_SkipsEmptyNamesAndDoesNotNormalizeCase()
    {
        var roots = SparkplugMetricHierarchy.Build(
            [Metric(""), Metric("   "), Metric("A/T1"), Metric("a/T1")]);

        roots.Select(root => root.Label).Should().Equal("A", "a");
        roots.Should().OnlyContain(root => root.Children.Count == 1);
    }

    [Test]
    public void Build_TreatsSlashesInsideParenthesesAsHierarchy()
    {
        var roots = SparkplugMetricHierarchy.Build([Metric("Flow Rate (L/min)", "12.5")]);

        var root = roots.Should().ContainSingle().Which;
        root.Label.Should().Be("Flow Rate (L");
        root.Children.Should().ContainSingle().Which.FullPath.Should().Be("Flow Rate (L/min)");
    }

    [Test]
    public void Build_DistinguishesEmptySegmentsWithoutOverwriting()
    {
        var roots = SparkplugMetricHierarchy.Build(
            [Metric("A/B"), Metric("A//B"), Metric("/A/B"), Metric("A/B/")]);

        var a = roots.Single(root => root.FullPath == "A");
        a.Children.Select(child => child.FullPath).Should().BeEquivalentTo("A/B", "A/");
        a.Children.Single(child => child.FullPath == "A/B").Metric!.Name.Should().Be("A/B");
        a.Children.Single(child => child.FullPath == "A/B").Children.Should().ContainSingle()
            .Which.FullPath.Should().Be("A/B/");
        a.Children.Single(child => child.FullPath == "A/").Children.Should().ContainSingle()
            .Which.FullPath.Should().Be("A//B");

        var leading = roots.Single(root => root.FullPath == "");
        leading.Label.Should().Be("(empty)");
        leading.Children.Should().ContainSingle().Which.FullPath.Should().Be("/A");
        leading.Children[0].Children.Should().ContainSingle().Which.FullPath.Should().Be("/A/B");

        var leaves = AllNodes(roots).Where(node => node.IsMetric).Select(node => node.FullPath).ToList();
        leaves.Should().OnlyHaveUniqueItems().And.HaveCount(4);

        var expanded = AllNodes(roots).Select(node => node.FullPath).ToHashSet(StringComparer.Ordinal);
        expanded.Should().Contain("");
        SparkplugMetricHierarchy.Flatten(roots, expanded)
            .Select(node => node.FullPath).Should().OnlyHaveUniqueItems();
    }

    [Test]
    public void Build_OrdersSegmentsNaturally()
    {
        var roots = SparkplugMetricHierarchy.Build([Metric("A/10"), Metric("A/2")]);

        roots.Single().Children.Select(child => child.Label).Should().Equal("2", "10");
    }

    [Test]
    public void Build_OrdersCaseAndZeroPaddingTiesDeterministically()
    {
        var roots = SparkplugMetricHierarchy.Build(
            [Metric("A2/T1"), Metric("A02/T1"), Metric("a2/T1")]);

        roots.Select(root => root.Label).Should().Equal("A02", "A2", "a2");
    }

    [Test]
    public void Flatten_ExpandsOnlyTheRequestedFolders()
    {
        var roots = SparkplugMetricHierarchy.Build([Metric("A/B/T1"), Metric("A/B/T2")]);

        SparkplugMetricHierarchy.Flatten(roots, new HashSet<string>())
            .Select(node => node.FullPath).Should().Equal("A");

        SparkplugMetricHierarchy.Flatten(roots, new HashSet<string> { "A", "A/B" })
            .Select(node => node.FullPath).Should().Equal("A", "A/B", "A/B/T1", "A/B/T2");
    }

    [Test]
    public void SingleChildPaths_StopsAtABranch()
    {
        var roots = SparkplugMetricHierarchy.Build([Metric("A/B/C/T1"), Metric("A/B/C/T2")]);

        SparkplugMetricHierarchy.SingleChildPaths(roots).Should().Equal("A", "A/B");
    }

    private static SparkplugMetricNode Descend(SparkplugMetricNode node)
    {
        while (node.HasChildren)
            node = node.Children.Should().ContainSingle().Which;

        return node;
    }

    private static IEnumerable<SparkplugMetricNode> AllNodes(IEnumerable<SparkplugMetricNode> roots)
    {
        foreach (var root in roots)
        {
            yield return root;
            foreach (var child in AllNodes(root.Children))
                yield return child;
        }
    }
}
