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
        bool groupSparkplugTopics) =>
        CreateRows(roots, filter, expandedPaths, groupSparkplugTopics, selectionPath: null, out _);

    // One pass builds the rows and reports whether the selected real path still has one,
    // ignoring filter and expansion, so a refresh never walks the store twice.
    public static List<TopicTreeRowModel> CreateRows(
        ImmutableArray<TopicNodeSnapshot> roots,
        string? filter,
        IReadOnlySet<string> expandedPaths,
        bool groupSparkplugTopics,
        string? selectionPath,
        out bool selectionRepresented)
    {
        var rows = new List<TopicTreeRowModel>();
        selectionRepresented = false;
        if (roots.IsDefaultOrEmpty)
            return rows;

        var hasFilter = !string.IsNullOrWhiteSpace(filter);

        // Grouping off walks the snapshot: cloning a mostly collapsed tree builds unseen rows.
        if (!groupSparkplugTopics)
        {
            selectionRepresented = selectionPath is null || ContainsRealPath(roots, selectionPath);
            foreach (var root in roots.OrderBy(node => node.Topic, NaturalStringComparer.Instance))
                AppendLiteralRows(root, 0, filter, hasFilter, expandedPaths, rows);

            return rows;
        }

        var built = BuildRoots(roots, groupSparkplugTopics: true);
        selectionRepresented = selectionPath is null || built.Any(root => ContainsPath(root, selectionPath));

        var context = new WalkContext(filter, hasFilter, expandedPaths);
        foreach (var root in InDisplayOrder(built))
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

    // Ignores expansion and filtering: a collapsed or filtered row still represents its topic.
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
        var separator = realPath.IndexOf('/', StringComparison.Ordinal);
        var segment = separator < 0 ? realPath : realPath[..separator];

        foreach (var node in nodes)
        {
            if (!string.Equals(node.Topic, segment, StringComparison.Ordinal))
                continue;

            return separator < 0
                || ContainsRealPath(node.Children, realPath[(separator + 1)..]);
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
            Kind: node.Kind,
            SelectionTopics: node.SelectionTopics(),
            Scope: node.Scope));

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

    // Matching follows the literal view: a row matches on the text it shows and keeps the
    // ancestors of a match, so a grouped leaf is findable by the whole topic path it shows.
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

        foreach (var branch in nodes.Values)
        {
            if (branch.Devices is not null)
                branch.Node.Children.Add(branch.Devices);
            built.Children.Add(branch.Node);
        }

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
                $"nm:{groupPath}/{tip.Topic}/{verb}", verb, TopicTreeRowKind.MessageType, MessageTypeOrder(verb));
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
                branch.Devices ??= VirtualRow(
                    $"dev:{groupPath}/{nodeLevel.Topic}", "Devices", TopicTreeRowKind.Devices, order: DeviceGroupOrder,
                    scope: new SparkplugSelectionScope(SparkplugSelectionKind.Devices, groupPath, nodeLevel.Topic));

                var deviceId = deviceNode.Topic;
                if (!branch.DeviceRows.TryGetValue(deviceId, out var deviceRow))
                {
                    deviceRow = VirtualRow(
                        $"device:{groupPath}/{nodeLevel.Topic}/{deviceId}",
                        deviceId, TopicTreeRowKind.SparkplugDevice, order: 0,
                        scope: new SparkplugSelectionScope(SparkplugSelectionKind.Device, groupPath, nodeLevel.Topic, deviceId));
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
        string id, string label, TopicTreeRowKind kind, int order, SparkplugSelectionScope? scope = null) => new()
        {
            Id = VirtualIdPrefix + id,
            Label = label,
            Kind = kind,
            Order = order,
            Scope = scope
        };

    // Node message types sort by lifecycle ahead of the Devices group, the node branch's only
    // other sibling.
    private const int DeviceGroupOrder = 10;

    // Birth, death, data, then command: the order an operator reads a node's lifecycle.
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

        public ProjectionNode? Devices { get; set; }

        public Dictionary<string, ProjectionNode> DeviceRows { get; } = new(StringComparer.Ordinal);
    }

    private sealed class ProjectionNode
    {
        public required string Id { get; init; }

        public required string Label { get; init; }

        public int Order { get; init; }

        public TopicTreeRowKind Kind { get; init; }

        public SparkplugSelectionScope? Scope { get; init; }

        public TopicNodeSnapshot? Source { get; init; }

        public List<ProjectionNode> Children { get; } = [];

        public bool IsRealTopic => Source is not null;

        public ImmutableArray<string> SelectionTopics() => IsRealTopic
            ? [Id]
            : [.. Children.SelectMany(child => child.SelectionTopics())];

        public int RealTopicCount => IsRealTopic
            ? Source!.TopicCount + 1
            : Children.Sum(child => child.RealTopicCount);

        public int MessageCount => IsRealTopic
            ? Source!.MessageCount
            : Children.Sum(child => child.MessageCount);
    }
}
