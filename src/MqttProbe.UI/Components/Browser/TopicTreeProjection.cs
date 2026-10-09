using System.Collections.Immutable;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Utilities;

namespace MqttProbe.UI.Components.Browser;

internal static class TopicTreeProjection
{
    public static List<TopicTreeRowModel> CreateRows(
        ImmutableArray<TopicNodeSnapshot> roots,
        string? filter,
        IReadOnlySet<string> expandedPaths)
    {
        var rows = new List<TopicTreeRowModel>();
        var hasFilter = !string.IsNullOrWhiteSpace(filter);
        if (roots.IsDefaultOrEmpty)
            return rows;

        foreach (var root in roots.OrderBy(node => node.Topic, NaturalStringComparer.Instance))
            AppendRows(root, 0, filter, hasFilter, expandedPaths, rows);

        return rows;
    }

    private static void AppendRows(
        TopicNodeSnapshot node,
        int depth,
        string? filter,
        bool hasFilter,
        IReadOnlySet<string> expandedPaths,
        List<TopicTreeRowModel> rows)
    {
        var matches = hasFilter && MatchesFilter(node, filter!);
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
            AppendRows(child, depth + 1, filter, hasFilter, expandedPaths, rows);
    }

    private static bool MatchesFilter(TopicNodeSnapshot node, string filter) =>
        node.Topic.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || (!node.Children.IsDefaultOrEmpty
            && node.Children.Any(child => MatchesFilter(child, filter)));
}
