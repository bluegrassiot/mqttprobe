using MqttProbe.Core.Models.Mqtt;

namespace MqttProbe.Core.Tests.Models.Mqtt;

[TestFixture]
public sealed class TopicSelectionMetricTests
{
    private const string GroupPath = "spBv1.0/plant1";

    [Test]
    public void ForSparkplugMetric_NodeOwner_BuildsAnExactMetricTarget()
    {
        var scope = new SparkplugSelectionScope(SparkplugSelectionKind.Node, GroupPath, "node1");

        var selection = TopicSelection.ForSparkplugMetric(scope, "A/B");

        selection.Mode.Should().Be(TopicSelectionMode.SparkplugMetric);
        selection.Metric.Should().Be(new MqttSparkplugMetricTarget(scope, "A/B"));
        selection.Topics.Should().BeEmpty();
        selection.Branches.Should().BeEmpty();
        selection.Scope.Should().BeNull();
        selection.Label.Should().Contain($"{GroupPath}/node1").And.Contain("A/B");
    }

    [Test]
    public void ForSparkplugMetric_DeviceOwner_UsesTheDeviceInTheLabel()
    {
        var scope = new SparkplugSelectionScope(SparkplugSelectionKind.Device, GroupPath, "node1", "dev1");

        var selection = TopicSelection.ForSparkplugMetric(scope, "Temperature");

        selection.Metric.Should().Be(new MqttSparkplugMetricTarget(scope, "Temperature"));
        selection.Label.Should().Contain($"{GroupPath}/node1/dev1").And.Contain("Temperature");
    }

    [Test]
    public void ForSparkplugMetric_IdentityDistinguishesOwnerAndExactName()
    {
        var keys = new[]
        {
            Key(new SparkplugSelectionScope(SparkplugSelectionKind.Node, GroupPath, "node1"), "A"),
            Key(new SparkplugSelectionScope(SparkplugSelectionKind.Node, GroupPath, "node1"), "A/B"),
            Key(new SparkplugSelectionScope(SparkplugSelectionKind.Node, GroupPath, "node1"), "a"),
            Key(new SparkplugSelectionScope(SparkplugSelectionKind.Node, GroupPath, "node2"), "A"),
            Key(new SparkplugSelectionScope(SparkplugSelectionKind.Device, GroupPath, "node1", "dev1"), "A"),
            Key(new SparkplugSelectionScope(SparkplugSelectionKind.Device, GroupPath, "node1", "dev2"), "A")
        };

        keys.Should().OnlyHaveUniqueItems();
    }

    [Test]
    public void ForSparkplugMetric_RejectsNonExactOwnersAndEmptyNames()
    {
        var devices = new SparkplugSelectionScope(SparkplugSelectionKind.Devices, GroupPath, "node1");
        var nodeWithDevice = new SparkplugSelectionScope(SparkplugSelectionKind.Device, GroupPath, "node1");
        var nodeWithId = new SparkplugSelectionScope(SparkplugSelectionKind.Node, GroupPath, "node1", "dev1");

        var act = () => TopicSelection.ForSparkplugMetric(devices, "A");
        act.Should().Throw<ArgumentException>();

        act = () => TopicSelection.ForSparkplugMetric(nodeWithDevice, "A");
        act.Should().Throw<ArgumentException>();

        act = () => TopicSelection.ForSparkplugMetric(nodeWithId, "A");
        act.Should().Throw<ArgumentException>();

        act = () => TopicSelection.ForSparkplugMetric(
            new SparkplugSelectionScope(SparkplugSelectionKind.Node, GroupPath, "node1"), string.Empty);
        act.Should().Throw<ArgumentException>();

        act = () => TopicSelection.ForSparkplugMetric(null!, "A");
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void ForSparkplugMetrics_CanonicalizesOrderAndDuplicatesIntoOneIdentity()
    {
        var node = NodeScope("node1");
        var device = DeviceScope("node1", "dev1");
        var first = new MqttSparkplugMetricTarget(node, "B");
        var second = new MqttSparkplugMetricTarget(device, "A");
        var third = new MqttSparkplugMetricTarget(node, "A/B");

        var selection = TopicSelection.ForSparkplugMetrics([first, second, third, first, second]);

        selection.Mode.Should().Be(TopicSelectionMode.SparkplugMetric);
        selection.Metrics.Should().HaveCount(3).And.OnlyHaveUniqueItems();
        selection.Metrics.Select(TopicSelection.ComposeMetricKey).Should()
            .BeInAscendingOrder(StringComparer.Ordinal);
        selection.Label.Should().Be("3 metrics");
        selection.Key.Should().StartWith("metrics:");

        var reordered = TopicSelection.ForSparkplugMetrics([third, first, second]);
        reordered.Key.Should().Be(selection.Key);
    }

    [Test]
    public void ForSparkplugMetrics_SingletonKeepsTheExistingKeyAndLabel()
    {
        var scope = NodeScope("node1");
        var target = new MqttSparkplugMetricTarget(scope, "A");

        var single = TopicSelection.ForSparkplugMetric(scope, "A");
        var fromSet = TopicSelection.ForSparkplugMetrics([target]);

        fromSet.Key.Should().Be(single.Key);
        fromSet.Label.Should().Be(single.Label);
        fromSet.Metric.Should().Be(target);
        fromSet.Metrics.Should().Equal(target);
    }

    [Test]
    public void ForSparkplugMetrics_EmptyInputBuildsADefensiveEmptyMetricSelection()
    {
        var selection = TopicSelection.ForSparkplugMetrics([]);

        selection.Mode.Should().Be(TopicSelectionMode.SparkplugMetric);
        selection.Metrics.Should().BeEmpty();
        selection.Metric.Should().BeNull();
        selection.Label.Should().Be("0 metrics");
    }

    [Test]
    public void ForSparkplugMetrics_RejectsAnInvalidTargetBeforeAnySelectionIsBuilt()
    {
        var valid = new MqttSparkplugMetricTarget(NodeScope("node1"), "A");
        var devices = new MqttSparkplugMetricTarget(
            new SparkplugSelectionScope(SparkplugSelectionKind.Devices, GroupPath, "node1"), "B");

        var act = () => TopicSelection.ForSparkplugMetrics([valid, devices]);
        act.Should().Throw<ArgumentException>();

        act = () => TopicSelection.ForSparkplugMetrics([valid, null!]);
        act.Should().Throw<ArgumentNullException>();

        act = () => TopicSelection.ForSparkplugMetrics([new MqttSparkplugMetricTarget(NodeScope("node1"), "")]);
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void MetricCompatibilityProperty_SharesTheSingleBackingCollection()
    {
        var node = NodeScope("node1");
        var target = new MqttSparkplugMetricTarget(node, "A");

        var selection = new TopicSelection("key", "label", []) { Metric = target };
        selection.Metrics.Should().Equal(target);
        selection.Metric.Should().Be(target);

        var cleared = selection with { Metric = null };
        cleared.Metrics.Should().BeEmpty();
        cleared.Metric.Should().BeNull();

        var multi = selection with
        {
            Metrics = [target, new MqttSparkplugMetricTarget(DeviceScope("node1", "dev1"), "B")]
        };
        multi.Metric.Should().BeNull("two metrics have no single compatibility target");
        multi.Metrics.Should().HaveCount(2);

        var backToSingle = multi with { Metric = target };
        backToSingle.Metrics.Should().Equal(target);

        var state = new SelectedTopicState(new SelectedTopicToken(Guid.NewGuid(), 1, 1), "key", 0)
        {
            Metric = target
        };
        state.Metrics.Should().Equal(target);

        var multiState = state with
        {
            Metrics = [target, new MqttSparkplugMetricTarget(DeviceScope("node1", "dev1"), "B")]
        };
        multiState.Metric.Should().BeNull();
        multiState.Metrics.Should().HaveCount(2);
        (multiState with { Metric = null }).Metrics.Should().BeEmpty();
    }

    [Test]
    public void MetricIdentity_KeepsCaseSlashesEmptyAndOwnerKindExact()
    {
        var target = new MqttSparkplugMetricTarget(NodeScope("node1"), "A");
        var selection = TopicSelection.ForSparkplugMetrics(
        [
            target,
            new MqttSparkplugMetricTarget(NodeScope("node1"), "a"),
            new MqttSparkplugMetricTarget(NodeScope("node1"), "A/B"),
            new MqttSparkplugMetricTarget(NodeScope("node1"), "A//B"),
            new MqttSparkplugMetricTarget(NodeScope("node2"), "A"),
            new MqttSparkplugMetricTarget(DeviceScope("node1", "dev1"), "A"),
            new MqttSparkplugMetricTarget(DeviceScope("node1", "dev2"), "A")
        ]);

        selection.Metrics.Should().HaveCount(7);
        selection.Metrics.Select(TopicSelection.ComposeMetricKey).Should().OnlyHaveUniqueItems();
    }

    private static SparkplugSelectionScope NodeScope(string nodeId) =>
        new(SparkplugSelectionKind.Node, GroupPath, nodeId);

    private static SparkplugSelectionScope DeviceScope(string nodeId, string deviceId) =>
        new(SparkplugSelectionKind.Device, GroupPath, nodeId, deviceId);

    private static string Key(SparkplugSelectionScope scope, string metricName) =>
        TopicSelection.ForSparkplugMetric(scope, metricName).Key;
}
