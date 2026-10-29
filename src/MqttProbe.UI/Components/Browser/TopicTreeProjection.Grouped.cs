using MqttProbe.Core.Models.Mqtt;

namespace MqttProbe.UI.Components.Browser;

internal static partial class TopicTreeProjection
{
    private static ProjectionNode BuildGroupedNamespace(TopicNodeSnapshot namespaceNode)
    {
        var built = RealRow(namespaceNode, TopicTreeRowKind.SparkplugNamespace);
        foreach (var group in namespaceNode.Children)
            built.Children.Add(BuildGroup(group.FullTopic, group));

        return built;
    }

    private static ProjectionNode BuildGroup(string groupPath, TopicNodeSnapshot groupNode)
    {
        var built = RealRow(groupNode, TopicTreeRowKind.SparkplugGroup);
        var nodes = new Dictionary<string, NodeBranch>(StringComparer.Ordinal);

        foreach (var verbNode in groupNode.Children)
        {
            var verb = verbNode.Topic;
            var groupedTips = new HashSet<string>(StringComparer.Ordinal);

            if (SparkplugTopicClassifier.IsNodeVerb(verb))
                AddNodeMessageTypes(nodes, groupPath, verbNode, verb, groupedTips);
            else if (SparkplugTopicClassifier.IsDeviceVerb(verb))
                AddDeviceMessages(nodes, groupPath, verbNode, verb, groupedTips);

            // Residue stays literal, keeping its real identity and source counts, so a malformed
            // message at a verb or device level never hides valid descendants.
            var residual = BuildResidual(verbNode, groupedTips);
            if (residual is not null)
                built.Children.Add(residual);
        }

        // Devices are ordinary children of the node, so the node holds its message-type groups
        // first and the device rows after them with no synthetic Devices wrapper between.
        foreach (var branch in nodes.Values)
            built.Children.Add(branch.Node);

        return built;
    }

    // Every row the partition did not take, minus the tips that moved into it: a structural
    // prefix survives, while a node that was ever an accepted endpoint always stays visible.
    private static ProjectionNode? BuildResidual(TopicNodeSnapshot node, HashSet<string> groupedTips)
    {
        if (groupedTips.Contains(node.FullTopic))
            return null;

        var built = RealRow(node, TopicTreeRowKind.Topic);
        foreach (var child in node.Children)
        {
            var childRow = BuildResidual(child, groupedTips);
            if (childRow is not null)
                built.Children.Add(childRow);
        }

        if (node.HasDirectMessages || node.HasObservedDirectTopic || node.Children.IsDefaultOrEmpty)
            return built;

        return built.Children.Count > 0 ? built : null;
    }

    private static void AddNodeMessageTypes(
        Dictionary<string, NodeBranch> nodes,
        string groupPath,
        TopicNodeSnapshot verbNode,
        string verb,
        HashSet<string> groupedTips)
    {
        foreach (var tip in verbNode.Children)
        {
            // The child of a node verb is the node topic itself, supported whenever a message was
            // ever accepted at that exact path. Only the canonical spelling counts:
            // "spBv1.0/g/NDATA//n1" normalizes onto the shape but is not itself an endpoint, so
            // it stays a prefix to group under rather than a node with messages.
            if (!tip.HasObservedCanonicalTopic)
                continue;

            groupedTips.Add(tip.FullTopic);
            var branch = GetOrCreateNode(nodes, groupPath, tip.Topic);

            var typeRow = VirtualRow(
                $"nm:{groupPath}/{tip.Topic}/{verb}", verb, TopicTreeRowKind.MessageType, MessageTypeOrder(verb),
                owner: TopicTreeOwner.Node);
            typeRow.Children.Add(GroupedTip(tip));
            branch.Node.Children.Add(typeRow);
        }
    }

    private static void AddDeviceMessages(
        Dictionary<string, NodeBranch> nodes,
        string groupPath,
        TopicNodeSnapshot verbNode,
        string verb,
        HashSet<string> groupedTips)
    {
        foreach (var nodeLevel in verbNode.Children)
        {
            foreach (var deviceNode in nodeLevel.Children)
            {
                // As at the node level the device tip classifies by the canonical observed marker
                // rather than its current messages, so a retention-emptied tip still groups and
                // one reached only through an alias spelling does not.
                if (!deviceNode.HasObservedCanonicalTopic)
                    continue;

                groupedTips.Add(deviceNode.FullTopic);
                var branch = GetOrCreateNode(nodes, groupPath, nodeLevel.Topic);

                var deviceId = deviceNode.Topic;
                if (!branch.DeviceRows.TryGetValue(deviceId, out var deviceRow))
                {
                    deviceRow = VirtualRow(
                        $"device:{groupPath}/{nodeLevel.Topic}/{deviceId}",
                        deviceId, TopicTreeRowKind.SparkplugDevice, order: DeviceRowOrder,
                        scope: new SparkplugSelectionScope(SparkplugSelectionKind.Device, groupPath, nodeLevel.Topic, deviceId));
                    branch.DeviceRows[deviceId] = deviceRow;
                    branch.Node.Children.Add(deviceRow);
                }

                var typeRow = VirtualRow(
                    $"devt:{groupPath}/{nodeLevel.Topic}/{deviceId}/{verb}",
                    verb, TopicTreeRowKind.MessageType, MessageTypeOrder(verb), owner: TopicTreeOwner.Device);
                typeRow.Children.Add(GroupedTip(deviceNode));
                deviceRow.Children.Add(typeRow);
            }
        }
    }

    private static NodeBranch GetOrCreateNode(
        Dictionary<string, NodeBranch> nodes,
        string groupPath,
        string nodeId)
    {
        if (nodes.TryGetValue(nodeId, out var existing))
            return existing;

        var branch = new NodeBranch
        {
            Node = VirtualRow($"node:{groupPath}/{nodeId}", nodeId, TopicTreeRowKind.SparkplugNode, order: 0,
                scope: new SparkplugSelectionScope(SparkplugSelectionKind.Node, groupPath, nodeId))
        };
        nodes[nodeId] = branch;
        return branch;
    }

    private static ProjectionNode RealRow(TopicNodeSnapshot node, TopicTreeRowKind kind) => new()
    {
        Id = node.FullTopic,
        Label = node.Topic,
        Kind = kind,
        Source = node
    };

    // A grouped leaf shows the real topic rather than its bare last segment, because the
    // grouping path is reordered.
    private static ProjectionNode GroupedTip(TopicNodeSnapshot tip)
    {
        var built = new ProjectionNode
        {
            Id = tip.FullTopic,
            Label = tip.FullTopic,
            Kind = TopicTreeRowKind.Topic,
            Source = tip
        };

        foreach (var child in tip.Children)
            built.Children.Add(BuildLiteral(child, TopicTreeRowKind.Topic));

        return built;
    }

    private static ProjectionNode VirtualRow(
        string id, string label, TopicTreeRowKind kind, int order,
        SparkplugSelectionScope? scope = null, TopicTreeOwner? owner = null) => new()
        {
            Id = VirtualIdPrefix + id,
            Label = label,
            Kind = kind,
            Order = order,
            Scope = scope,
            Owner = owner
        };

    // Node message types sort by lifecycle ahead of the device rows, the node branch's other
    // children, so every node type sits above every device.
    private const int DeviceRowOrder = 10;

    // Birth, death, data, then command: the order an operator reads a node's lifecycle.
    private static int MessageTypeOrder(string verb) => verb switch
    {
        "NBIRTH" or "DBIRTH" => 0,
        "NDEATH" or "DDEATH" => 1,
        "NDATA" or "DDATA" => 2,
        "NCMD" or "DCMD" => 3,
        _ => 4
    };

}
