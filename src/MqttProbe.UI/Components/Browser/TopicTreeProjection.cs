using System.Collections.Immutable;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Utilities;

namespace MqttProbe.UI.Components.Browser;

internal static class TopicTreeProjection
{
    // U+0000 cannot appear in an MQTT topic, so a virtual grouping id can never collide
    // with a real topic path in expansion state, and can never be selected as a topic.
    private const string VirtualIdPrefix = "\0spb:";

    public static List<TopicTreeRowModel> CreateRows(
        ImmutableArray<TopicNodeSnapshot> roots,
        string? filter,
        IReadOnlySet<string> expandedPaths,
        bool groupSparkplugTopics)
    {
        var rows = new List<TopicTreeRowModel>();
        if (roots.IsDefaultOrEmpty)
            return rows;

        var hasFilter = !string.IsNullOrWhiteSpace(filter);

        // Grouping off walks the snapshot itself: cloning a tree that is mostly collapsed
        // would build nodes for rows the caller will never see.
        if (!groupSparkplugTopics)
        {
            foreach (var root in roots.OrderBy(node => node.Topic, NaturalStringComparer.Instance))
                AppendLiteralRows(root, 0, filter, hasFilter, expandedPaths, rows);

            return rows;
        }

        var context = new WalkContext(filter, hasFilter, expandedPaths);
        foreach (var root in InDisplayOrder(BuildRoots(roots, groupSparkplugTopics: true)))
            AppendRows(root, 0, context, rows);

        return rows;
    }

    public static void AddExpandablePaths(
        ImmutableArray<TopicNodeSnapshot> roots,
        bool groupSparkplugTopics,
        ISet<string> target)
    {
        if (roots.IsDefaultOrEmpty)
            return;

        if (!groupSparkplugTopics)
        {
            foreach (var root in roots)
                AddLiteralExpandablePaths(root, target);

            return;
        }

        foreach (var root in BuildRoots(roots, groupSparkplugTopics: true))
            AddExpandablePaths(root, target);
    }

    // Whether the projection presents this real store path as a row, ignoring expansion and
    // filtering: a row hidden only because its branch is collapsed or filtered out still
    // represents the topic and must not be treated as gone.
    public static bool RepresentsRealPath(
        ImmutableArray<TopicNodeSnapshot> roots,
        bool groupSparkplugTopics,
        string? realPath)
    {
        if (string.IsNullOrEmpty(realPath) || roots.IsDefaultOrEmpty)
            return false;

        if (!groupSparkplugTopics)
            return ContainsRealPath(roots, realPath);

        return BuildRoots(roots, groupSparkplugTopics: true).Any(root => ContainsPath(root, realPath));
    }

    public static bool IsVirtualExpansionId(string id) =>
        id.StartsWith(VirtualIdPrefix, StringComparison.Ordinal);

    private static void AppendLiteralRows(
        TopicNodeSnapshot node,
        int depth,
        string? filter,
        bool hasFilter,
        IReadOnlySet<string> expandedPaths,
        List<TopicTreeRowModel> rows)
    {
        var matches = hasFilter && MatchesLiteralFilter(node, filter!);
        if (hasFilter && !matches)
            return;

        var hasChildren = !node.Children.IsDefaultOrEmpty;
        var isExpanded = expandedPaths.Contains(node.FullTopic);
        rows.Add(new TopicTreeRowModel(
            FullPath: node.FullTopic,
            DisplayName: node.Topic,
            Depth: depth,
            HasChildren: hasChildren,
            TopicCount: node.TopicCount,
            MessageCount: node.MessageCount,
            IsExpanded: isExpanded || (hasFilter && matches),
            IsValueBearer: node.HasDirectMessages));

        if (!hasChildren || (!isExpanded && !hasFilter))
            return;

        foreach (var child in node.Children.OrderBy(item => item.Topic, NaturalStringComparer.Instance))
            AppendLiteralRows(child, depth + 1, filter, hasFilter, expandedPaths, rows);
    }

    private static bool MatchesLiteralFilter(TopicNodeSnapshot node, string filter) =>
        node.Topic.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || (!node.Children.IsDefaultOrEmpty
            && node.Children.Any(child => MatchesLiteralFilter(child, filter)));

    private static void AddLiteralExpandablePaths(TopicNodeSnapshot node, ISet<string> target)
    {
        if (node.Children.IsDefaultOrEmpty)
            return;

        target.Add(node.FullTopic);
        foreach (var child in node.Children)
            AddLiteralExpandablePaths(child, target);
    }

    private static bool ContainsRealPath(ImmutableArray<TopicNodeSnapshot> nodes, string realPath)
    {
        foreach (var node in nodes)
        {
            if (string.Equals(node.FullTopic, realPath, StringComparison.Ordinal))
                return true;

            if (ContainsRealPath(node.Children, realPath))
                return true;
        }

        return false;
    }

    private static bool ContainsPath(ProjectionNode node, string realPath)
    {
        if (string.Equals(node.Id, realPath, StringComparison.Ordinal))
            return true;

        return node.Children.Any(child => ContainsPath(child, realPath));
    }

    private static void AppendRows(
        ProjectionNode node,
        int depth,
        in WalkContext context,
        List<TopicTreeRowModel> rows)
    {
        var matches = context.HasFilter && Matches(node, context.Filter!);
        if (context.HasFilter && !matches)
            return;

        var hasChildren = node.Children.Count > 0;
        var isExpanded = context.ExpandedPaths.Contains(node.Id) || (context.HasFilter && matches);

        rows.Add(new TopicTreeRowModel(
            FullPath: node.Id,
            DisplayName: node.Label,
            Depth: depth,
            HasChildren: hasChildren,
            TopicCount: node.IsRealTopic ? node.Source!.TopicCount : node.RealTopicCount,
            MessageCount: node.MessageCount,
            IsExpanded: isExpanded,
            IsValueBearer: node.IsRealTopic && node.Source!.HasDirectMessages,
            Kind: node.Kind));

        if (!hasChildren || (!isExpanded && !context.HasFilter))
            return;

        foreach (var child in InDisplayOrder(node.Children))
            AppendRows(child, depth + 1, context, rows);
    }

    private static void AddExpandablePaths(ProjectionNode node, ISet<string> target)
    {
        if (node.Children.Count == 0)
            return;

        target.Add(node.Id);
        foreach (var child in node.Children)
            AddExpandablePaths(child, target);
    }

    // Matching follows the literal view: a row matches on the text it shows, and an
    // ancestor is kept for a matching descendant. A literal row shows its own path
    // segment, while a grouped real row shows the whole topic path, so a grouped leaf can
    // also be found by the topic or path fragment the user can actually read.
    private static bool Matches(ProjectionNode node, string filter)
    {
        if (node.Label.Contains(filter, StringComparison.OrdinalIgnoreCase))
            return true;

        return node.Children.Any(child => Matches(child, filter));
    }

    private static IEnumerable<ProjectionNode> InDisplayOrder(IEnumerable<ProjectionNode> nodes) =>
        nodes.OrderBy(node => node.Order).ThenBy(node => node.Label, NaturalStringComparer.Instance);

    private static List<ProjectionNode> BuildRoots(
        ImmutableArray<TopicNodeSnapshot> roots,
        bool groupSparkplugTopics)
    {
        var result = new List<ProjectionNode>(roots.Length);
        foreach (var root in roots)
        {
            if (groupSparkplugTopics
                && string.Equals(root.Topic, SparkplugTopicClassifier.Namespace, StringComparison.Ordinal))
                result.Add(BuildGroupedNamespace(root));
            else
                result.Add(BuildLiteral(root, TopicTreeRowKind.Topic));
        }

        return result;
    }

    private static ProjectionNode BuildLiteral(TopicNodeSnapshot node, TopicTreeRowKind kind)
    {
        var built = RealRow(node, kind);
        foreach (var child in node.Children)
            built.Children.Add(BuildLiteral(child, TopicTreeRowKind.Topic));

        return built;
    }

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
            if (!CanRegroupVerb(verbNode, verb))
            {
                // STATE, unknown verbs and shapes that do not match the supported topic
                // stay in the literal hierarchy rather than being forced into it.
                built.Children.Add(BuildLiteral(verbNode, TopicTreeRowKind.Topic));
                continue;
            }

            if (SparkplugTopicClassifier.IsNodeVerb(verb))
                AddNodeMessages(nodes, groupPath, verbNode, verb);
            else
                AddDeviceMessages(nodes, groupPath, verbNode, verb);
        }

        foreach (var branch in nodes.Values)
        {
            if (branch.NodeMessages is not null)
                branch.Node.Children.Add(branch.NodeMessages);
            if (branch.Devices is not null)
                branch.Node.Children.Add(branch.Devices);
            built.Children.Add(branch.Node);
        }

        return built;
    }

    private static bool CanRegroupVerb(TopicNodeSnapshot verbNode, string verb)
    {
        if (verbNode.HasDirectMessages || verbNode.Children.IsDefaultOrEmpty)
            return false;

        if (SparkplugTopicClassifier.IsNodeVerb(verb))
        {
            // A node verb's child is the node-level topic itself. It is a supported topic
            // only when the message landed on it: a child with no messages of its own only
            // exists because a malformed deeper segment did, so the branch stays literal.
            return verbNode.Children.All(node => node.HasDirectMessages);
        }

        if (!SparkplugTopicClassifier.IsDeviceVerb(verb))
            return false;

        // Device verbs add a node level the supported shape never publishes to, and each
        // device below it must carry the message rather than a malformed deeper segment.
        // Deciding per verb branch keeps every topic either fully grouped or fully literal.
        return verbNode.Children.All(node => !node.HasDirectMessages)
            && verbNode.Children.All(node => node.Children.All(device => device.HasDirectMessages));
    }

    private static void AddNodeMessages(
        Dictionary<string, NodeBranch> nodes,
        string groupPath,
        TopicNodeSnapshot verbNode,
        string verb)
    {
        foreach (var tip in verbNode.Children)
        {
            var branch = GetOrCreateNode(nodes, groupPath, tip.Topic);
            branch.NodeMessages ??= VirtualRow(
                $"nm:{groupPath}/{tip.Topic}", "Node messages", TopicTreeRowKind.NodeMessages, order: 0);

            var typeRow = VirtualRow(
                $"nm:{groupPath}/{tip.Topic}/{verb}", verb, TopicTreeRowKind.MessageType, MessageTypeOrder(verb));
            typeRow.Children.Add(GroupedTip(tip));
            branch.NodeMessages.Children.Add(typeRow);
        }
    }

    private static void AddDeviceMessages(
        Dictionary<string, NodeBranch> nodes,
        string groupPath,
        TopicNodeSnapshot verbNode,
        string verb)
    {
        foreach (var nodeLevel in verbNode.Children)
        {
            var branch = GetOrCreateNode(nodes, groupPath, nodeLevel.Topic);
            branch.Devices ??= VirtualRow(
                $"dev:{groupPath}/{nodeLevel.Topic}", "Devices", TopicTreeRowKind.Devices, order: 1);

            foreach (var deviceNode in nodeLevel.Children)
            {
                var deviceId = deviceNode.Topic;
                if (!branch.DeviceRows.TryGetValue(deviceId, out var deviceRow))
                {
                    deviceRow = VirtualRow(
                        $"device:{groupPath}/{nodeLevel.Topic}/{deviceId}",
                        deviceId, TopicTreeRowKind.SparkplugDevice, order: 0);
                    branch.DeviceRows[deviceId] = deviceRow;
                    branch.Devices.Children.Add(deviceRow);
                }

                var typeRow = VirtualRow(
                    $"devt:{groupPath}/{nodeLevel.Topic}/{deviceId}/{verb}",
                    verb, TopicTreeRowKind.MessageType, MessageTypeOrder(verb));
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
            Node = VirtualRow($"node:{groupPath}/{nodeId}", nodeId, TopicTreeRowKind.SparkplugNode, order: 0)
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

    // A grouped leaf shows the real topic rather than its bare last segment: the grouping
    // path is reordered, so the row itself has to carry the topic the message arrived on.
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

    private static ProjectionNode VirtualRow(string id, string label, TopicTreeRowKind kind, int order) => new()
    {
        Id = VirtualIdPrefix + id,
        Label = label,
        Kind = kind,
        Order = order
    };

    // Birth, death, data, then command - the order an operator reads a node's lifecycle,
    // with the commands that drive it last.
    private static int MessageTypeOrder(string verb) => verb switch
    {
        "NBIRTH" or "DBIRTH" => 0,
        "NDEATH" or "DDEATH" => 1,
        "NDATA" or "DDATA" => 2,
        "NCMD" or "DCMD" => 3,
        _ => 4
    };

    private readonly record struct WalkContext(string? Filter, bool HasFilter, IReadOnlySet<string> ExpandedPaths);

    private sealed class NodeBranch
    {
        public required ProjectionNode Node { get; init; }

        public ProjectionNode? NodeMessages { get; set; }

        public ProjectionNode? Devices { get; set; }

        public Dictionary<string, ProjectionNode> DeviceRows { get; } = new(StringComparer.Ordinal);
    }

    private sealed class ProjectionNode
    {
        public required string Id { get; init; }

        public required string Label { get; init; }

        public int Order { get; init; }

        public TopicTreeRowKind Kind { get; init; }

        public TopicNodeSnapshot? Source { get; init; }

        public List<ProjectionNode> Children { get; } = [];

        public bool IsRealTopic => Source is not null;

        public int RealTopicCount => IsRealTopic
            ? Source!.TopicCount + 1
            : Children.Sum(child => child.RealTopicCount);

        public int MessageCount => IsRealTopic
            ? Source!.MessageCount
            : Children.Sum(child => child.MessageCount);
    }
}
