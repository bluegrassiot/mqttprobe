using System.Collections.Immutable;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Utilities;

namespace MqttProbe.UI.Components.Browser;

internal static partial class TopicTreeProjection
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
        out bool selectionRepresented,
        bool selectionMode = false)
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

        var context = new WalkContext(filter, hasFilter, expandedPaths, selectionMode);
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

    // Bulk selection names raw spellings, so representation follows the observed provenance
    // rather than the normalized node id. A grouped view only shows canonical tips, so an alias
    // spelling it cannot show is not represented there.
    public static bool RepresentsObservedPath(
        ImmutableArray<TopicNodeSnapshot> roots,
        bool groupSparkplugTopics,
        string? rawPath)
    {
        if (string.IsNullOrEmpty(rawPath) || roots.IsDefaultOrEmpty)
            return false;

        var node = FindByObservedPath(roots, rawPath);
        if (node is null)
            return false;

        if (!groupSparkplugTopics || node.HasObservedCanonicalTopic)
            return true;

        return BuildRoots(roots, groupSparkplugTopics: true)
            .Any(root => ContainsPath(root, node.FullTopic));
    }

    private static TopicNodeSnapshot? FindByObservedPath(
        ImmutableArray<TopicNodeSnapshot> nodes, string rawPath)
    {
        foreach (var node in nodes)
        {
            if (!node.ObservedTopicPaths.IsDefaultOrEmpty
                && node.ObservedTopicPaths.Contains(rawPath, StringComparer.Ordinal))
                return node;

            if (FindByObservedPath(node.Children, rawPath) is { } child)
                return child;
        }

        return null;
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
            IsValueBearer: node.HasDirectMessages,
            IsMessageTopic: node.HasObservedDirectTopic || node.HasDirectMessages)
        {
            Branch = new TopicBranchTarget(TopicBranchKind.Raw, Path: node.FullTopic),
            BranchLabel = node.FullTopic
        });

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
            Scope: node.Scope,
            Owner: node.Owner,
            IsMessageTopic: node.IsRealTopic
                && (node.Source!.HasObservedDirectTopic || node.Source.HasDirectMessages))
        {
            Branch = node.Target,
            BranchLabel = node.BranchLabel
        });

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

    private readonly record struct WalkContext(
        string? Filter, bool HasFilter, IReadOnlySet<string> ExpandedPaths, bool SelectionMode);

    private sealed class NodeBranch
    {
        public required ProjectionNode Node { get; init; }

        public Dictionary<string, ProjectionNode> DeviceRows { get; } = new(StringComparer.Ordinal);
    }

    private sealed class ProjectionNode
    {
        public required string Id { get; init; }

        public required string Label { get; init; }

        public int Order { get; init; }

        public TopicTreeRowKind Kind { get; init; }

        public SparkplugSelectionScope? Scope { get; init; }

        public TopicTreeOwner? Owner { get; init; }

        public TopicNodeSnapshot? Source { get; init; }

        public TopicBranchTarget Target { get; init; } = null!;

        public string BranchLabel { get; init; } = string.Empty;

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
