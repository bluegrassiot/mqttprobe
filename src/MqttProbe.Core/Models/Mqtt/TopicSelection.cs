using System.Collections.Immutable;
using System.Text;

namespace MqttProbe.Core.Models.Mqtt;

public enum SparkplugSelectionKind
{
    Node,
    Devices,
    Device
}

public sealed record SparkplugSelectionScope(
    SparkplugSelectionKind Kind,
    string GroupPath,
    string NodeId,
    string? DeviceId = null);

public sealed record TopicSelection(
    string Key,
    string Label,
    ImmutableArray<string> Topics,
    SparkplugSelectionScope? Scope = null,
    bool RequireCanonicalTopics = false)
{
    public TopicSelectionMode Mode { get; init; } = TopicSelectionMode.SingleTopic;

    public ImmutableArray<TopicBranchTarget> Branches { get; init; } = ImmutableArray<TopicBranchTarget>.Empty;

    public MqttSparkplugMetricTarget? Metric { get; init; }

    public static TopicSelection ForTopic(string fullTopic) =>
        new(fullTopic, fullTopic, [fullTopic]);

    public static TopicSelection ForExactTopics(IEnumerable<string> fullTopics)
    {
        var paths = fullTopics.Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        var label = paths.Length == 1 ? paths[0] : $"{paths.Length} topics selected";
        return new(ComposeExactKey(paths), label, [.. paths]) { Mode = TopicSelectionMode.MultipleTopics };
    }

    public static TopicSelection ForBranches(IEnumerable<TopicBranchTarget> branches)
    {
        ArgumentNullException.ThrowIfNull(branches);
        var targets = branches
            .Where(branch => branch is not null)
            .Distinct()
            .OrderBy(BranchIdentity, StringComparer.Ordinal)
            .ToArray();

        var label = targets.Length switch
        {
            0 => "No branches selected",
            1 => BranchLabel(targets[0]),
            _ => $"{targets.Length} branches selected"
        };

        return new(ComposeBranchKey(targets), label, ImmutableArray<string>.Empty)
        {
            Mode = TopicSelectionMode.Branches,
            Branches = [.. targets]
        };
    }

    public static TopicSelection ForSparkplugMetric(SparkplugSelectionScope scope, string metricName)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrEmpty(metricName);
        if (string.IsNullOrEmpty(scope.GroupPath) || string.IsNullOrEmpty(scope.NodeId))
            throw new ArgumentException("A Sparkplug metric owner needs a group and node.", nameof(scope));

        var device = scope.Kind switch
        {
            SparkplugSelectionKind.Node when scope.DeviceId is null => null,
            SparkplugSelectionKind.Device when !string.IsNullOrEmpty(scope.DeviceId) => scope.DeviceId,
            _ => throw new ArgumentException("A Sparkplug metric owner must be a node or a device.", nameof(scope))
        };

        var target = new MqttSparkplugMetricTarget(scope, metricName);
        var owner = $"{scope.GroupPath}/{scope.NodeId}";
        if (device is not null)
            owner += "/" + device;

        return new(ComposeMetricKey(target), $"{owner} {metricName}", ImmutableArray<string>.Empty)
        {
            Mode = TopicSelectionMode.SparkplugMetric,
            Metric = target
        };
    }

    internal static string ComposeExactKey(IEnumerable<string> paths)
    {
        var builder = new StringBuilder("multi:");
        foreach (var path in paths.Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal))
            builder.Append(path.Length).Append(':').Append(path);

        return builder.ToString();
    }

    internal static string ComposeBranchKey(IEnumerable<TopicBranchTarget> branches)
    {
        var builder = new StringBuilder("branches:");
        foreach (var identity in branches.Select(BranchIdentity).Distinct(StringComparer.Ordinal)
                     .OrderBy(identity => identity, StringComparer.Ordinal))
            builder.Append(identity.Length).Append(':').Append(identity);

        return builder.ToString();
    }

    internal static string ComposeMetricKey(MqttSparkplugMetricTarget target)
    {
        var builder = new StringBuilder("metric:");
        AppendIdentityField(builder, target.Scope.Kind.ToString());
        AppendIdentityField(builder, target.Scope.GroupPath);
        AppendIdentityField(builder, target.Scope.NodeId);
        AppendIdentityField(builder, target.Scope.DeviceId);
        AppendIdentityField(builder, target.MetricName);
        return builder.ToString();
    }

    internal static string BranchIdentity(TopicBranchTarget branch)
    {
        var builder = new StringBuilder();
        if (branch.Kind == TopicBranchKind.Raw)
        {
            builder.Append("R:");
            AppendIdentityField(builder, branch.Path);
            return builder.ToString();
        }

        if (branch.Kind != TopicBranchKind.Sparkplug)
            return $"?:{(int)branch.Kind}";

        builder.Append("S:");
        AppendIdentityField(builder, branch.Scope is { } scope ? scope.Kind.ToString() : null);
        AppendIdentityField(builder, branch.Scope?.GroupPath);
        AppendIdentityField(builder, branch.Scope?.NodeId);
        AppendIdentityField(builder, branch.Scope?.DeviceId);
        AppendIdentityField(builder, branch.MessageType);
        return builder.ToString();
    }

    // Length-prefixed so a delimiter inside a field cannot alias a different field split.
    private static void AppendIdentityField(StringBuilder builder, string? value)
    {
        if (value is null)
        {
            builder.Append("-;");
            return;
        }

        builder.Append(value.Length).Append(':').Append(value).Append(';');
    }

    internal static string BranchLabel(TopicBranchTarget branch) =>
        branch.Kind switch
        {
            TopicBranchKind.Raw => branch.Path is { Length: > 0 } path ? path : "/",
            TopicBranchKind.Sparkplug => SparkplugBranchLabel(branch),
            _ => string.Empty
        };

    private static string SparkplugBranchLabel(TopicBranchTarget branch)
    {
        if (branch.Scope is not { } scope)
            return string.Empty;

        var label = scope.GroupPath + "/" + scope.NodeId;
        if (scope.DeviceId is { Length: > 0 } device)
            label += "/" + device;
        if (branch.MessageType is { Length: > 0 } type)
            label += " " + type;

        return label;
    }
}
