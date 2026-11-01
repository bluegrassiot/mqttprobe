using System.Collections.Immutable;
using System.Runtime.InteropServices;
using MqttProbe.Core.Models.Mqtt;

namespace MqttProbe.Core.Services.Mqtt;

public interface ITopicExcludeService : IDisposable
{
    public IReadOnlyList<string> TopicExcludes { get; }
    public IReadOnlyList<SparkplugTopicExclude> SparkplugExcludes { get; }
    public TopicExcludeValidationResult ValidateAdd(string topic);
    public bool IsExcluded(string topic);
    public Task<TopicExcludeOperationResult> Add(string topic);
    public Task<TopicExcludeBatchResult> AddMany(IReadOnlyList<string> topics);
    public TopicExcludeConnectionToken CaptureConnectionToken();
    public bool TryExecuteForConnection<T>(
        TopicExcludeConnectionToken token, Func<T> mutation, out T result);
    public Task<TopicBranchExcludeBatchResult> AddBranches(
        IReadOnlyList<TopicBranchTarget> targets, TopicExcludeConnectionToken token);
    public Task<TopicExcludeOperationResult> Remove(IReadOnlyList<string> topics);
    public Task<TopicExcludeOperationResult> RemoveBranches(IReadOnlyList<TopicBranchTarget> targets);
    public void ClearActiveTopicExcludes();

    public event Action<string>? PurgeExcludedTopic;
    public event Action<string>? TopicExcluded;
    public event Action<SparkplugTopicExclude>? PurgeExcludedSparkplug;
    public event Action<SparkplugTopicExclude>? SparkplugExcluded;
    public IDisposable? TryEnter(string topic);
}

public sealed record TopicExcludeValidationResult(bool IsValid, UserNotification? Feedback = null);
public sealed record TopicExcludeOperationResult(bool IsValid, UserNotification? Feedback = null);

[StructLayout(LayoutKind.Auto)]
public readonly record struct TopicExcludeConnectionToken(
    long Generation, Guid ConnectionId, long SelectionRevision = 0);

public enum TopicExcludeAddOutcome
{
    Added,
    AlreadyExcluded,
    LimitReached,
    Invalid,
    Failed,
    AppliedWithPurgeFailure,
    ConnectionChanged,
    SavedButNotApplied
}

public sealed record TopicExcludeAddResult(
    string Topic,
    TopicExcludeAddOutcome Outcome,
    UserNotification? Feedback = null);

public sealed record TopicExcludeBatchResult(ImmutableArray<TopicExcludeAddResult> Results)
{
    public bool AllSucceeded =>
        Results.All(result => result.Outcome is TopicExcludeAddOutcome.Added or TopicExcludeAddOutcome.AlreadyExcluded);

    public ImmutableArray<string> AddedTopics =>
        [.. Results.Where(result => result.Outcome == TopicExcludeAddOutcome.Added).Select(result => result.Topic)];

    public ImmutableArray<string> AppliedTopics =>
        [.. Results.Where(result => result.Outcome is TopicExcludeAddOutcome.Added or TopicExcludeAddOutcome.AppliedWithPurgeFailure)
            .Select(result => result.Topic)];
}

public sealed record TopicBranchExcludeResult(
    TopicBranchTarget Target,
    TopicExcludeAddOutcome Outcome,
    UserNotification? Feedback = null);

public sealed record TopicBranchExcludeBatchResult(ImmutableArray<TopicBranchExcludeResult> Results)
{
    public bool AllSucceeded =>
        Results.All(result => result.Outcome is TopicExcludeAddOutcome.Added or TopicExcludeAddOutcome.AlreadyExcluded);

    public ImmutableArray<TopicBranchTarget> AddedTargets =>
        [.. Results.Where(result => result.Outcome == TopicExcludeAddOutcome.Added).Select(result => result.Target)];

    public ImmutableArray<TopicBranchTarget> AppliedTargets =>
        [.. Results.Where(result => result.Outcome is TopicExcludeAddOutcome.Added or TopicExcludeAddOutcome.AppliedWithPurgeFailure)
            .Select(result => result.Target)];
}
