namespace MqttProbe.Core.Models.Mqtt;

public readonly record struct SparkplugTopicPath(
    string GroupPath,
    string Verb,
    string NodeId,
    string? DeviceId);

public static class TopicBranchMatcher
{
    public static bool Matches(TopicBranchTarget? target, string? topic) =>
        target switch
        {
            { Kind: TopicBranchKind.Raw } => MatchesRaw(topic, target.Path),
            { Kind: TopicBranchKind.Sparkplug } => MatchesSparkplug(target.Scope, target.MessageType, topic),
            _ => false
        };

    // Ordinal equality or a slash boundary, so "a/b" covers "a/b/c" but never "a/bc". An empty
    // path is the structural root and covers only the slash-leading topics whose first level is empty.
    public static bool MatchesRaw(string? topic, string? path)
    {
        if (topic is null || path is null)
            return false;

        if (path.Length == 0)
            return topic.Length > 0 && topic[0] == '/';

        return string.Equals(topic, path, StringComparison.Ordinal)
            || (topic.Length > path.Length
                && topic.StartsWith(path, StringComparison.Ordinal)
                && topic[path.Length] == '/');
    }

    public static bool MatchesSparkplug(SparkplugSelectionScope? scope, string? messageType, string? topic)
    {
        if (scope is null || topic is null)
            return false;

        // Endpoints live under "<group>/<verb>/<nodeId>" and may carry a device and further
        // descendants, so coverage is decided on that prefix instead of a full-path parse.
        var prefix = scope.GroupPath + "/";
        if (!topic.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var segments = topic[prefix.Length..].Split('/');
        if (segments.Length < 2 || segments[0].Length == 0 || segments[1].Length == 0)
            return false;

        var verb = segments[0];
        if (!string.Equals(segments[1], scope.NodeId, StringComparison.Ordinal))
            return false;

        if (messageType is not null && !string.Equals(verb, messageType, StringComparison.Ordinal))
            return false;

        // A node selection covers the node endpoint, its device endpoints and any future
        // verb or descendant below it.
        if (scope.Kind == SparkplugSelectionKind.Node)
            return true;

        if (segments.Length < 3 || segments[2].Length == 0)
            return false;

        var device = segments[2];
        if (!IsDeviceEndpointVerb(verb))
            return false;

        return scope.Kind == SparkplugSelectionKind.Devices
            || scope.DeviceId is null
            || string.Equals(device, scope.DeviceId, StringComparison.Ordinal);
    }

    // Device endpoints follow the D-prefixed verb convention, so a future device type is covered
    // without confusing it with a descendant of a node endpoint such as NDATA/<node>/<level>.
    private static bool IsDeviceEndpointVerb(string verb) =>
        SparkplugTopicClassifier.IsDeviceVerb(verb) || verb[0] == 'D';

    // Endpoint parsing only; subtree coverage belongs to MatchesSparkplug.
    public static bool TryParseSparkplug(string? topic, out SparkplugTopicPath parsed)
    {
        parsed = default;
        if (topic is null)
            return false;

        var segments = topic.Split('/');
        if (segments.Length is not (4 or 5))
            return false;

        if (!string.Equals(segments[0], SparkplugTopicClassifier.Namespace, StringComparison.Ordinal)
            || segments[1].Length == 0
            || segments[2].Length == 0
            || segments[3].Length == 0)
            return false;

        if (segments.Length == 5 && segments[4].Length == 0)
            return false;

        parsed = new SparkplugTopicPath(
            $"{SparkplugTopicClassifier.Namespace}/{segments[1]}",
            segments[2],
            segments[3],
            segments.Length == 5 ? segments[4] : null);
        return true;
    }
}
