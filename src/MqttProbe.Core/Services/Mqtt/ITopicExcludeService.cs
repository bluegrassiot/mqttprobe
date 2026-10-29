using System.Collections.Immutable;

namespace MqttProbe.Core.Services.Mqtt;

public interface ITopicExcludeService : IDisposable
{
    public IReadOnlyList<string> TopicExcludes { get; }
    public TopicExcludeValidationResult ValidateAdd(string topic);
    public bool IsExcluded(string topic);
    public Task<TopicExcludeOperationResult> Add(string topic);
    public Task<TopicExcludeBatchResult> AddMany(IReadOnlyList<string> topics);
    public Task<TopicExcludeOperationResult> Remove(IReadOnlyList<string> topics);
    public void ClearActiveTopicExcludes();

    public event Action<string>? PurgeExcludedTopic;
    public event Action<string>? TopicExcluded;
    public IDisposable? TryEnter(string topic);
}

public sealed record TopicExcludeValidationResult(bool IsValid, UserNotification? Feedback = null);
public sealed record TopicExcludeOperationResult(bool IsValid, UserNotification? Feedback = null);

public enum TopicExcludeAddOutcome
{
    Added,
    AlreadyExcluded,
    LimitReached,
    Invalid,
    Failed,
    AppliedWithPurgeFailure
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
