using System.Collections.Immutable;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Models.Sparkplug;

namespace MqttProbe.UI.Tests.Components.Browser;

[TestFixture]
public sealed class TopicTreeMetricProjectionTests
{
    private static readonly SparkplugSelectionScope _nodeScope =
        new(SparkplugSelectionKind.Node, "spBv1.0/plant1", "node1");

    private static readonly SparkplugSelectionScope _deviceScope =
        new(SparkplugSelectionKind.Device, "spBv1.0/plant1", "node1", "dev1");

    private static ImmutableArray<TopicNodeSnapshot> Tree() => TopicSnapshotBuilder.Build(
        ("spBv1.0/plant1/NDATA/node1", 2),
        ("spBv1.0/plant1/DDATA/node1/dev1", 1));

    private static SpbMetricSnapshot Metric(string name, string value = "1", string type = "int32") =>
        new(name, type, value, DateTime.UnixEpoch);

    private static MetricProjection Projection(
        IReadOnlyList<SpbMetricSnapshot>? nodeMetrics = null,
        IReadOnlyList<SpbMetricSnapshot>? deviceMetrics = null,
        SparkplugSelectionScope? selectedOwner = null) =>
        new(scope => scope.Kind == SparkplugSelectionKind.Node
                ? nodeMetrics ?? [Metric("Motor/RPM"), Metric("Temp")]
                : deviceMetrics ?? [Metric("Speed/Value")],
            selectedOwner);

    private static List<TopicTreeRowModel> Rows(
        ImmutableArray<TopicNodeSnapshot> roots,
        MetricProjection? metrics,
        string? filter = null,
        HashSet<string>? expanded = null,
        bool grouped = true)
    {
        var ids = expanded ?? Expanded(roots, grouped, metrics);
        return TopicTreeProjection.CreateRows(
            roots, filter, ids, grouped, selectionPath: null, out _, out _, metrics);
    }

    private static HashSet<string> Expanded(
        ImmutableArray<TopicNodeSnapshot> roots, bool grouped, MetricProjection? metrics)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        TopicTreeProjection.AddExpandablePaths(roots, grouped, ids, metrics);
        return ids;
    }

    private static TopicTreeRowModel Row(List<TopicTreeRowModel> rows, string label) =>
        rows.Single(row => row.DisplayName == label);

    [Test]
    public void GroupedRows_ShowNestedNodeAndDeviceMetrics()
    {
        var rows = Rows(Tree(), Projection());

        var nodeFolder = Row(rows, "Motor");
        nodeFolder.IsMetricFolder.Should().BeTrue();
        nodeFolder.Scope.Should().Be(_nodeScope);
        nodeFolder.Owner.Should().Be(TopicTreeOwner.Node);
        nodeFolder.MetricName.Should().BeNull("a pure folder is expanded, not selected");
        nodeFolder.Branch.Should().BeNull("a metric row never stands for a transport topic");

        var rpm = Row(rows, "RPM");
        rpm.MetricName.Should().Be("Motor/RPM");
        rpm.MetricValue.Should().Be("1");
        rpm.Scope.Should().Be(_nodeScope);
        rpm.IsMetricFolder.Should().BeFalse();

        var speedFolder = Row(rows, "Speed");
        speedFolder.Scope.Should().Be(_deviceScope);
        speedFolder.Owner.Should().Be(TopicTreeOwner.Device);
        Row(rows, "Value").MetricName.Should().Be("Speed/Value");
        rows.Where(row => row.IsMetricRow)
            .Should().OnlyContain(row => row.FullPath.StartsWith("\0spb:metric:", StringComparison.Ordinal));
    }

    [Test]
    public void AAndAB_CoexistAsSelectableValueAndFolder()
    {
        var rows = Rows(Tree(), Projection([Metric("A", "5"), Metric("A/B", "6")]));

        var a = Row(rows, "A");
        a.MetricName.Should().Be("A");
        a.HasChildren.Should().BeTrue("A also owns the nested metric A/B");
        Row(rows, "B").MetricName.Should().Be("A/B");
    }

    [Test]
    public void EmptySegmentsStayDistinctMetricIdentities()
    {
        var rows = Rows(Tree(), Projection(
            [Metric("A/B"), Metric("A//B"), Metric("/A/B"), Metric("A/B/")], []));

        var values = rows.Where(row => row.MetricName is not null).ToList();
        values.Select(row => row.MetricName).Should()
            .BeEquivalentTo(["A/B", "A//B", "/A/B", "A/B/"]);
        values.Select(row => row.BranchLabel).Should().OnlyHaveUniqueItems(
            "the exact metric name stays the row identity");
        values.Select(row => row.FullPath).Should().OnlyHaveUniqueItems();
    }

    [Test]
    public void EmptyOrUnresolvedMetricNamesAreSkipped()
    {
        var rows = Rows(Tree(), Projection([Metric(" "), Metric(string.Empty), Metric("Named")], []));

        rows.Where(row => row.IsMetricRow).Select(row => row.MetricName)
            .Should().Equal("Named");
    }

    [Test]
    public void LiteralMode_HasNoMetricRows()
    {
        var rows = Rows(Tree(), Projection(), grouped: false);

        rows.Should().NotContain(row => row.IsMetricRow);
    }

    [Test]
    public void EveryOwnerGetsItsOwnMetricsGroup()
    {
        var rows = Rows(Tree(), Projection());

        var groups = rows.Where(row => row.IsMetricGroup).ToList();
        groups.Should().HaveCount(2);
        groups.Select(row => row.Scope).Should().Equal(_nodeScope, _deviceScope);
        groups.Should().OnlyContain(row => row.DisplayName == "Metrics" && row.HasChildren);
        groups.Select(row => row.MetricCount).Should().Equal(2, 1);
        groups.Should().OnlyContain(row => row.Branch == null,
            "a metrics group is a container, never a selectable branch");
        groups.Should().OnlyContain(row => !row.CanSelect);
        groups.Should().OnlyContain(row => row.IsVirtualTopic,
            "the group is virtual, so it never reads as a real topic path");
        groups.SelectMany(row => row.MetricTargets).Should().HaveCount(3);
    }

    [Test]
    public void MetricTargets_IncludeTheOwnValueAndEveryKnownDescendant()
    {
        var rows = Rows(Tree(), Projection([Metric("A", "1"), Metric("A/B", "2")]));

        var a = Row(rows, "A");
        var group = rows.Single(row => row.IsMetricGroup && row.Scope == _nodeScope);
        a.MetricTargets.Select(target => target.MetricName).Should().Equal("A", "A/B");
        group.MetricTargets.Select(target => target.MetricName).Should().Equal("A", "A/B");
        a.MetricTargets.Should().OnlyContain(target => target.Scope == _nodeScope);
    }

    [Test]
    public void MetricTargets_FollowNewDescendantsFromTopology()
    {
        var before = Rows(Tree(), Projection([Metric("A/B")]));
        var beforeFolder = before.Single(row => row.IsMetricGroup && row.Scope == _nodeScope);

        var after = Rows(Tree(), Projection([Metric("A/B"), Metric("A/C")]));
        var afterFolder = after.Single(row => row.IsMetricGroup && row.Scope == _nodeScope);

        beforeFolder.MetricTargets.Should().ContainSingle()
            .Which.MetricName.Should().Be("A/B");
        afterFolder.MetricTargets.Select(target => target.MetricName).Should().Equal("A/B", "A/C");
    }

    [Test]
    public void RepresentedMetricOwners_UsesObservedOwnerRowsOnly()
    {
        var present = TopicTreeProjection.RepresentedMetricOwners(
            Tree(), groupSparkplugTopics: true, [_nodeScope, _deviceScope]);
        var absent = TopicTreeProjection.RepresentedMetricOwners(
            Tree(), groupSparkplugTopics: true,
            [new SparkplugSelectionScope(SparkplugSelectionKind.Node, "spBv1.0/plant1", "node9")]);

        present.Should().BeEquivalentTo([_nodeScope, _deviceScope]);
        absent.Should().BeEmpty();
        TopicTreeProjection.RepresentedMetricOwners(Tree(), groupSparkplugTopics: false, [_nodeScope])
            .Should().BeEmpty("the literal view has no metric owners");
    }

    [Test]
    public void MetricsGroupSitsAfterTheOwnersMessageTypesAndBeforeItsDevices()
    {
        var rows = Rows(
            TopicSnapshotBuilder.Build(
                ("spBv1.0/plant1/NDATA/node1", 2),
                ("spBv1.0/plant1/NBIRTH/node1", 1),
                ("spBv1.0/plant1/DDATA/node1/dev1", 1)),
            Projection(deviceMetrics: [Metric("Speed")]));

        rows.Select(row => row.DisplayName).Should().Equal(
            "spBv1.0", "plant1", "node1", "NBIRTH", "spBv1.0/plant1/NBIRTH/node1",
            "NDATA", "spBv1.0/plant1/NDATA/node1", "Metrics", "Motor", "RPM", "Temp",
            "dev1", "DDATA", "spBv1.0/plant1/DDATA/node1/dev1", "Metrics", "Speed");
    }

    [Test]
    public void MetricsGroupIdentityNeverCollidesWithATopicPathOrAMetricName()
    {
        var rows = Rows(Tree(), Projection([Metric(""), Metric(string.Empty), Metric("Metrics")]));

        var identities = rows.Select(row => row.FullPath).ToList();
        identities.Should().OnlyHaveUniqueItems();
        rows.Where(row => row.IsMetricGroup)
            .Select(row => row.FullPath)
            .Should().OnlyContain(id => TopicTreeProjection.IsVirtualExpansionId(id)
                && id.Contains("spb:metrics:", StringComparison.Ordinal));
        rows.Where(row => row.IsMetricRow)
            .Select(row => row.FullPath)
            .Should().OnlyContain(id => id.Contains("spb:metric:", StringComparison.Ordinal)
                && !id.Contains("spb:metrics:", StringComparison.Ordinal));
    }

    [Test]
    public void MetricsDoNotChangeTopicOrMessageTotals()
    {
        var withMetrics = Rows(Tree(), Projection());
        var withoutMetrics = Rows(Tree(), metrics: null);

        var nodeWith = withMetrics.Single(row => row.Kind == TopicTreeRowKind.SparkplugNode);
        var nodeWithout = withoutMetrics.Single(row => row.Kind == TopicTreeRowKind.SparkplugNode);
        nodeWith.TopicCount.Should().Be(nodeWithout.TopicCount);
        nodeWith.MessageCount.Should().Be(nodeWithout.MessageCount);

        var namespaceRow = withMetrics[0];
        namespaceRow.TopicCount.Should().Be(withoutMetrics[0].TopicCount);
        namespaceRow.MessageCount.Should().Be(withoutMetrics[0].MessageCount);
    }

    [Test]
    public void AGroupExpandsAndCollapsesWithoutSelecting()
    {
        var expanded = Expanded(Tree(), grouped: true, Projection());
        Rows(Tree(), Projection(), expanded: expanded)
            .Should().Contain(row => row.IsMetricRow && row.DisplayName == "RPM");

        var collapsed = new HashSet<string>(expanded, StringComparer.Ordinal);
        collapsed.RemoveWhere(id => id.Contains("spb:metrics:", StringComparison.Ordinal));
        var rows = Rows(Tree(), Projection(), expanded: collapsed);

        rows.Should().Contain(row => row.IsMetricGroup && !row.IsExpanded);
        rows.Should().NotContain(row => row.IsMetricRow);
    }

    [Test]
    public void FilterOnTheExactMetricPath_RetainsAncestors()
    {
        var rows = Rows(Tree(), Projection(), filter: "Motor/RPM");

        rows.Select(row => row.DisplayName).Should().Contain(["plant1", "node1", "Metrics", "Motor", "RPM"]);
        rows.Should().NotContain(row => row.DisplayName == "Temp");
        Row(rows, "RPM").HasChildren.Should().BeFalse();
        Row(rows, "Motor").IsExpanded.Should().BeTrue("a filter opens the ancestor chain");
        Row(rows, "Metrics").IsExpanded.Should().BeTrue("the group stays as the metric's ancestor");
    }

    [Test]
    public void CollapsedMetricFolder_HidesItsChildren()
    {
        var expanded = new HashSet<string>(StringComparer.Ordinal);
        TopicTreeProjection.AddExpandablePaths(Tree(), groupSparkplugTopics: true, expanded, Projection());
        var rows = Rows(Tree(), Projection(), expanded: expanded);

        rows.Should().Contain(row => row.IsMetricRow && row.DisplayName == "RPM");

        var collapsed = new HashSet<string>(StringComparer.Ordinal);
        TopicTreeProjection.AddExpandablePaths(Tree(), groupSparkplugTopics: true, collapsed, Projection());
        collapsed.RemoveWhere(id => id.Contains(":metric:", StringComparison.Ordinal));
        var collapsedRows = Rows(Tree(), Projection(), expanded: collapsed);

        collapsedRows.Should().NotContain(row => row.DisplayName == "RPM");
        collapsedRows.Should().Contain(row => row.DisplayName == "Motor" && row.IsMetricFolder && !row.IsExpanded);
    }

    [Test]
    public void MetricValueSurvivesAnOfflineOwner()
    {
        var rows = Rows(Tree(), Projection([Metric("Temp", "-12.5", "double")]));

        var temp = Row(rows, "Temp");
        temp.MetricValue.Should().Be("-12.5");
        temp.MetricNumeric.Should().BeTrue();
    }

    [Test]
    public void OwnerRepresentation_FollowsTheObservedStoreRow()
    {
        var present = Rows(Tree(), Projection(selectedOwner: _nodeScope));
        var absent = Rows(Tree(), Projection(selectedOwner: new SparkplugSelectionScope(
            SparkplugSelectionKind.Node, "spBv1.0/plant1", "node9")));

        present.Should().NotBeEmpty();
        absent.Should().NotBeEmpty();
        MetricOwnerRepresented(Tree(), _nodeScope).Should().BeTrue();
        MetricOwnerRepresented(Tree(), new SparkplugSelectionScope(
            SparkplugSelectionKind.Node, "spBv1.0/plant1", "node9")).Should().BeFalse(
            "topology cannot invent an owner the store never observed");
    }

    [Test]
    public void ChartBindsToTheObservedDataTip()
    {
        var rows = Rows(Tree(), Projection());

        Row(rows, "RPM").MetricChartTopic.Should().Be("spBv1.0/plant1/NDATA/node1");
        Row(rows, "Value").MetricChartTopic.Should().Be("spBv1.0/plant1/DDATA/node1/dev1");
        Row(rows, "RPM").MetricChartable.Should().BeTrue();

        var birthOnly = TopicSnapshotBuilder.Build(("spBv1.0/plant1/NBIRTH/node1", 1));
        var birthRows = Rows(birthOnly, Projection());

        Row(birthRows, "RPM").MetricChartTopic.Should().BeNull(
            "no chart topic is synthesized when the data tip was never observed");
    }

    private static bool MetricOwnerRepresented(
        ImmutableArray<TopicNodeSnapshot> roots, SparkplugSelectionScope scope)
    {
        TopicTreeProjection.CreateRows(
            roots, null, new HashSet<string>(), groupSparkplugTopics: true,
            selectionPath: null, out _, out var represented,
            metrics: Projection(selectedOwner: scope));
        return represented;
    }
}
