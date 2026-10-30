using MqttProbe.Core.Models.Mqtt;

namespace MqttProbe.UI.Tests.Components.Browser;

[TestFixture]
public sealed class TopicBranchTargetRelationsTests
{
    [Test]
    public void ARawGroupRow_CoversTheLogicalRowsBeneathIt()
    {
        var group = Raw("spBv1.0/plant1");

        TopicBranchTargetRelations.Covers(group, Node("spBv1.0/plant1", "node1")).Should().BeTrue();
        TopicBranchTargetRelations.Covers(group, Device("spBv1.0/plant1", "node1", "dev1")).Should().BeTrue();
        TopicBranchTargetRelations.Covers(group, Node("spBv1.0/plant1", "node1", "NDATA")).Should().BeTrue();
        TopicBranchTargetRelations.Covers(Raw("spBv1.0"), Node("spBv1.0/plant1", "node1")).Should().BeTrue();
        TopicBranchTargetRelations.Covers(group, Node("spBv1.0/plant2", "node1")).Should().BeFalse();
        TopicBranchTargetRelations.Covers(Raw("spBv1.0/plant1x"), Node("spBv1.0/plant1", "node1")).Should().BeFalse();
    }

    [Test]
    public void ALogicalScope_CoversTheRealRowsBeneathIt()
    {
        var node = Node("spBv1.0/plant1", "node1");

        TopicBranchTargetRelations.Covers(node, Raw("spBv1.0/plant1/NDATA/node1")).Should().BeTrue();
        TopicBranchTargetRelations.Covers(node, Raw("spBv1.0/plant1/DDATA/node1/dev1")).Should().BeTrue();
        TopicBranchTargetRelations.Covers(node, Raw("spBv1.0/plant1")).Should().BeFalse();
        TopicBranchTargetRelations.Covers(node, Raw("spBv1.0/plant1/NDATA/node2")).Should().BeFalse();
    }

    [Test]
    public void ARawTip_MakesItsLogicalAncestorsMixed()
    {
        var tip = Raw("spBv1.0/plant1/NDATA/node1");

        TopicBranchTargetRelations.IsUnder(tip, Node("spBv1.0/plant1", "node1")).Should().BeTrue();
        TopicBranchTargetRelations.IsUnder(Node("spBv1.0/plant1", "node1"), tip).Should().BeFalse();
        TopicBranchTargetRelations.IsUnder(Node("spBv1.0/plant1", "node1"), Raw("spBv1.0/plant1")).Should().BeTrue();
        TopicBranchTargetRelations.IsUnder(tip, Raw("spBv1.0/plant1")).Should().BeTrue();
    }

    [Test]
    public void TheEmptyFirstLevel_CoversOnlySlashLeadingRawRows()
    {
        var empty = Raw(string.Empty);

        TopicBranchTargetRelations.Covers(empty, empty).Should().BeTrue();
        TopicBranchTargetRelations.Covers(empty, Raw("/a")).Should().BeTrue();
        TopicBranchTargetRelations.Covers(empty, Raw("a")).Should().BeFalse();
    }

    [Test]
    public void AMessageTypeScope_NarrowsItsNodeScope()
    {
        var type = Node("spBv1.0/plant1", "node1", "NDATA");

        TopicBranchTargetRelations.Covers(type, Node("spBv1.0/plant1", "node1")).Should().BeFalse();
        TopicBranchTargetRelations.Covers(Node("spBv1.0/plant1", "node1"), type).Should().BeTrue();
        TopicBranchTargetRelations.Covers(type, Raw("spBv1.0/plant1/NDATA/node1")).Should().BeTrue();
        TopicBranchTargetRelations.Covers(type, Raw("spBv1.0/plant1/NBIRTH/node1")).Should().BeFalse();
    }

    [Test]
    public void ARawVerbRow_CoversTheTypeRowItNames_AndOnlyPartOfTheNode()
    {
        var verb = Raw("spBv1.0/plant1/NDATA");

        TopicBranchTargetRelations.Covers(verb, Node("spBv1.0/plant1", "node1", "NDATA"))
            .Should().BeTrue("every topic the NDATA row names is under the verb row");
        TopicBranchTargetRelations.Covers(verb, Node("spBv1.0/plant1", "node1"))
            .Should().BeFalse("the node row also names other verbs");
        TopicBranchTargetRelations.PartiallyOverlaps(verb, Node("spBv1.0/plant1", "node1"))
            .Should().BeTrue();
        TopicBranchTargetRelations.Covers(verb, Node("spBv1.0/plant1", "node1", "NBIRTH"))
            .Should().BeFalse();
        TopicBranchTargetRelations.PartiallyOverlaps(verb, Node("spBv1.0/plant1", "node1", "NBIRTH"))
            .Should().BeFalse();
        TopicBranchTargetRelations.PartiallyOverlaps(verb, Device("spBv1.0/plant1", "node1", "dev1"))
            .Should().BeFalse("a node verb names no device endpoint");
    }

    [Test]
    public void ARawNodeEndpoint_CoversItsTypeRow_AndOnlyPartOfTheNodeScope()
    {
        var endpoint = Raw("spBv1.0/plant1/NDATA/node1");

        TopicBranchTargetRelations.Covers(endpoint, Node("spBv1.0/plant1", "node1", "NDATA"))
            .Should().BeTrue();
        TopicBranchTargetRelations.Covers(endpoint, Node("spBv1.0/plant1", "node1"))
            .Should().BeFalse();
        TopicBranchTargetRelations.IsUnder(endpoint, Node("spBv1.0/plant1", "node1"))
            .Should().BeTrue("the node scope covers the endpoint, so the node row is mixed");
        TopicBranchTargetRelations.PartiallyOverlaps(endpoint, Node("spBv1.0/plant1", "node1"))
            .Should().BeFalse();
        TopicBranchTargetRelations.Covers(endpoint, Node("spBv1.0/plant1", "node2", "NDATA"))
            .Should().BeFalse();
        TopicBranchTargetRelations.PartiallyOverlaps(endpoint, Node("spBv1.0/plant1", "node2", "NDATA"))
            .Should().BeFalse();
    }

    [Test]
    public void ADeviceScope_ReachesOnlyPartOfItsNodeLevel()
    {
        var device = Device("spBv1.0/plant1", "node1", "dev1");
        var nodeLevel = Raw("spBv1.0/plant1/DDATA/node1");

        TopicBranchTargetRelations.Covers(device, nodeLevel).Should().BeFalse();
        TopicBranchTargetRelations.PartiallyOverlaps(device, nodeLevel).Should().BeTrue();
        TopicBranchTargetRelations.PartiallyOverlaps(Raw("spBv1.0/plant1/NDATA/node1"), device)
            .Should().BeFalse("a node verb names no device endpoint");
    }

    [Test]
    public void ARawDeviceEndpoint_IsOnlyOneVerbOfTheDeviceScope()
    {
        var device = Device("spBv1.0/plant1", "node1", "dev1");
        var endpoint = Raw("spBv1.0/plant1/DDATA/node1/dev1");

        TopicBranchTargetRelations.Covers(endpoint, device).Should().BeFalse(
            "the device row also names its other verbs");
        TopicBranchTargetRelations.IsUnder(endpoint, device).Should().BeTrue(
            "the device scope covers the endpoint, so the device row is mixed");
        TopicBranchTargetRelations.PartiallyOverlaps(endpoint, device).Should().BeFalse();
        TopicBranchTargetRelations.Covers(endpoint, Device("spBv1.0/plant1", "node1", "dev1", "DDATA"))
            .Should().BeTrue("the typed device row names only that endpoint");
        TopicBranchTargetRelations.Covers(endpoint, Device("spBv1.0/plant1", "node1", "dev2"))
            .Should().BeFalse();
        TopicBranchTargetRelations.PartiallyOverlaps(endpoint, Device("spBv1.0/plant1", "node1", "dev2"))
            .Should().BeFalse();
    }

    private static TopicBranchTarget Raw(string path) => new(TopicBranchKind.Raw, Path: path);

    private static TopicBranchTarget Node(string groupPath, string nodeId, string? messageType = null) =>
        new(TopicBranchKind.Sparkplug,
            Scope: new SparkplugSelectionScope(SparkplugSelectionKind.Node, groupPath, nodeId),
            MessageType: messageType);

    private static TopicBranchTarget Device(
        string groupPath, string nodeId, string deviceId, string? messageType = null) =>
        new(TopicBranchKind.Sparkplug,
            Scope: new SparkplugSelectionScope(SparkplugSelectionKind.Device, groupPath, nodeId, deviceId),
            MessageType: messageType);
}
