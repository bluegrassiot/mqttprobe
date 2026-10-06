namespace MqttProbe.Core.Services.Mqtt;

public interface ITopicExcludeService : IDisposable
{
    public IReadOnlyList<string> TopicExcludes { get; }
    public TopicExcludeValidationResult ValidateAdd(string topic);
    public bool IsExcluded(string topic);
    public Task<TopicExcludeOperationResult> Add(string topic);
    public Task<TopicExcludeOperationResult> Remove(IReadOnlyList<string> topics);
    public void ClearActiveTopicExcludes();

    public event Action<string>? PurgeExcludedTopic;
    public event Action<string>? TopicExcluded;
    public IDisposable? TryEnter(string topic);
}

public sealed record TopicExcludeValidationResult(bool IsValid, UserNotification? Feedback = null);
public sealed record TopicExcludeOperationResult(bool IsValid, UserNotification? Feedback = null);
