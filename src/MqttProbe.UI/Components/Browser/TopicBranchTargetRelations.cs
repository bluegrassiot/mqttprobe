using MqttProbe.Core.Models.Mqtt;

namespace MqttProbe.UI.Components.Browser;

// Coverage follows the topic space each target names: a raw row above a scope covers every topic
// the scope can resolve, and a logical scope covers the real rows beneath it. A raw prefix that
// only reaches part of a scope (a verb or node level) is an overlap, not coverage.
internal static class TopicBranchTargetRelations
{
    public static bool Covers(TopicBranchTarget selected, TopicBranchTarget row)
    {
        if (selected.Kind == TopicBranchKind.Raw)
        {
            if (row.Kind == TopicBranchKind.Raw)
                return string.Equals(row.Path, selected.Path, StringComparison.Ordinal)
                    || TopicBranchMatcher.MatchesRaw(row.Path, selected.Path);

            return CoversLogicalRow(row, selected.Path);
        }

        if (row.Kind == TopicBranchKind.Raw)
            return TopicBranchMatcher.MatchesSparkplug(selected.Scope, selected.MessageType, row.Path);

        if (selected.Scope is null || row.Scope is null)
            return false;

        if (!ScopeCovers(selected.Scope, row.Scope))
            return false;

        return selected.MessageType is null
            || string.Equals(selected.MessageType, row.MessageType, StringComparison.Ordinal);
    }

    public static bool IsUnder(TopicBranchTarget candidate, TopicBranchTarget ancestor) =>
        !Equals(candidate, ancestor) && Covers(ancestor, candidate);

    // Cross-kind only: a raw prefix can reach part of a logical scope without covering it all.
    public static bool PartiallyOverlaps(TopicBranchTarget selected, TopicBranchTarget row)
    {
        if (selected.Kind == row.Kind)
            return false;

        return selected.Kind == TopicBranchKind.Raw
            ? PartiallyInside(row, selected.Path)
            : PartiallyInside(selected, row.Path);
    }

    // Every topic a logical row names is selected when the raw path is at or above the scope's
    // widest endpoint: the group for an untyped scope, the typed endpoint otherwise.
    private static bool CoversLogicalRow(TopicBranchTarget row, string? rawPath)
    {
        if (row.Scope is not { } scope)
            return false;

        var prefix = WidestPrefix(scope, row.MessageType);
        return string.Equals(prefix, rawPath, StringComparison.Ordinal)
            || TopicBranchMatcher.MatchesRaw(prefix, rawPath);
    }

    private static string WidestPrefix(SparkplugSelectionScope scope, string? messageType)
    {
        if (messageType is not { } type)
            return scope.GroupPath;

        return scope.Kind == SparkplugSelectionKind.Device
            ? $"{scope.GroupPath}/{type}/{scope.NodeId}/{scope.DeviceId}"
            : $"{scope.GroupPath}/{type}/{scope.NodeId}";
    }

    // True when the raw path sits strictly inside the group and reaches some, but not all, of the
    // scope's endpoints: a verb level, or a node level for a device scope.
    private static bool PartiallyInside(TopicBranchTarget logical, string? rawPath)
    {
        if (logical.Scope is not { } scope
            || rawPath is null
            || rawPath.Length <= scope.GroupPath.Length + 1
            || !TopicBranchMatcher.MatchesRaw(rawPath, scope.GroupPath))
            return false;

        var rest = rawPath[(scope.GroupPath.Length + 1)..].Split('/');
        if (rest[0].Length == 0 || !AdmitsVerb(scope, logical.MessageType, rest[0]))
            return false;

        if (rest.Length == 1)
            return true;

        if (!string.Equals(rest[1], scope.NodeId, StringComparison.Ordinal))
            return false;

        return rest.Length == 2 && scope.Kind != SparkplugSelectionKind.Node;
    }

    private static bool AdmitsVerb(SparkplugSelectionScope scope, string? messageType, string verb)
    {
        if (messageType is { } type)
            return string.Equals(verb, type, StringComparison.Ordinal);

        // A node scope admits any verb, including ones that arrive later.
        if (scope.Kind == SparkplugSelectionKind.Node)
            return true;

        // Same device-endpoint convention the shared matcher applies to a device verb.
        return SparkplugTopicClassifier.IsDeviceVerb(verb) || verb[0] == 'D';
    }

    private static bool ScopeCovers(SparkplugSelectionScope selected, SparkplugSelectionScope row)
    {
        if (!string.Equals(selected.GroupPath, row.GroupPath, StringComparison.Ordinal))
            return false;

        if (!string.Equals(selected.NodeId, row.NodeId, StringComparison.Ordinal))
            return false;

        if (selected.Kind == SparkplugSelectionKind.Node)
            return row.Kind is SparkplugSelectionKind.Node
                or SparkplugSelectionKind.Device
                or SparkplugSelectionKind.Devices;

        if (selected.Kind == SparkplugSelectionKind.Device)
        {
            return row.Kind == SparkplugSelectionKind.Device
                && string.Equals(selected.DeviceId, row.DeviceId, StringComparison.Ordinal);
        }

        if (selected.Kind == SparkplugSelectionKind.Devices)
            return row.Kind is SparkplugSelectionKind.Device or SparkplugSelectionKind.Devices;

        return false;
    }
}
