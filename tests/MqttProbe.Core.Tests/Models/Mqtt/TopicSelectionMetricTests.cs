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

    private static string Key(SparkplugSelectionScope scope, string metricName) =>
        TopicSelection.ForSparkplugMetric(scope, metricName).Key;
}
