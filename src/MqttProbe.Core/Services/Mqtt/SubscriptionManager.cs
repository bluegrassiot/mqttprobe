using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Protocol;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Configuration;

namespace MqttProbe.Core.Services.Mqtt;

public interface ISubscriptionManager : IDisposable
{
    public IReadOnlyList<SubscribedTopic> Subscriptions { get; }
    public Task Remove(IReadOnlyList<string> topics);
    public Task Add(string topic, MqttQualityOfServiceLevel qos = MqttQualityOfServiceLevel.AtLeastOnce);
    public void ClearActiveSubscriptions();
}

public class SubscriptionManager : ISubscriptionManager
{
    private readonly IMqttManagedClient _managedMqttClient;
    private readonly ILogger<SubscriptionManager> _logger;
    private readonly IUserNotifier _notifier;
    private readonly IConnectionSettings _connectionSettings;
    private readonly IUiSettings _uiSettings;
    private readonly ISessionState _sessionState;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly Lock _topicsSync = new();
    private readonly Dictionary<string, MqttQualityOfServiceLevel> _topics = new(StringComparer.Ordinal);

    public SubscriptionManager(IMqttManagedClient managedMqttClient, ILogger<SubscriptionManager> logger,
        IUserNotifier notifier, IConnectionSettings connectionSettings, IUiSettings uiSettings, ISessionState sessionState)
    {
        _managedMqttClient = managedMqttClient;
        _logger = logger;
        _notifier = notifier;
        _connectionSettings = connectionSettings;
        _uiSettings = uiSettings;
        _sessionState = sessionState;
        _managedMqttClient.ConnectedAsync += OnConnected;
        _managedMqttClient.SynchronizingSubscriptionsFailedAsync += OnSyncFailed;
    }

    private const int MaxSubscriptions = 500;

    // S2365: this is a real copy (the live dictionary needs its lock released
    // before returning), but it's public API surface consumed by third-party
    // plugins as a property — converting it to a method is a breaking change.
#pragma warning disable S2365 // Properties should not make collection copies
    public IReadOnlyList<SubscribedTopic> Subscriptions
    {
        get
        {
            lock (_topicsSync)
            {
                return _topics
                    .Select(kv => new SubscribedTopic
                    {
                        Topic = kv.Key,
                        QualityOfServiceLevel = kv.Value
                    })
                    .ToList();
            }
        }
    }
#pragma warning restore S2365

    public async Task Remove(IReadOnlyList<string> topics)
    {
        await _operationLock.WaitAsync();
        try
        {
            await _managedMqttClient.UnsubscribeAsync(topics);

            lock (_topicsSync)
            {
                foreach (var topic in topics)
                {
                    _topics.Remove(topic);
                    if (_logger.IsEnabled(LogLevel.Information))
                        _logger.LogInformation("Unsubscribed from topic {Topic}", topic);
                }
            }

            await PersistTopicsAsync();
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task Add(string topic, MqttQualityOfServiceLevel qos = MqttQualityOfServiceLevel.AtLeastOnce)
    {
        if (string.IsNullOrWhiteSpace(topic) || topic.Contains('\0') || topic.Length > 65_535)
        {
            _notifier.Notify(new UserNotification(UserNotificationSeverity.Warning, "Invalid topic"));
            _logger.LogWarning("Rejected invalid subscription topic (length={Len})", topic.Length);
            return;
        }

        await _operationLock.WaitAsync();
        try
        {
            lock (_topicsSync)
            {
                if (_topics.ContainsKey(topic))
                {
                    _notifier.Notify(new UserNotification(UserNotificationSeverity.Warning, $"Already subscribed to {topic}"));
                    return;
                }

                if (_topics.Count >= MaxSubscriptions)
                {
                    _notifier.Notify(new UserNotification(UserNotificationSeverity.Warning, $"Subscription limit ({MaxSubscriptions}) reached"));
                    _logger.LogWarning("Subscription limit ({Limit}) reached; topic {Topic} not added",
                        MaxSubscriptions, topic);
                    return;
                }
            }

            var topicFilter = new MqttTopicFilterBuilder()
                .WithTopic(topic)
                .WithQualityOfServiceLevel(qos)
                .Build();

            await _managedMqttClient.SubscribeAsync([topicFilter]);

            lock (_topicsSync)
            {
                _topics[topic] = qos;
            }

            _notifier.Notify(new UserNotification(UserNotificationSeverity.Success, $"Subscribed to {topic}"));
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Subscribed to topic {Topic}", topic);

            await PersistTopicsAsync();
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public void ClearActiveSubscriptions()
    {
        lock (_topicsSync)
        {
            _topics.Clear();
        }
    }

    private async Task OnConnected(MqttClientConnectedEventArgs args)
    {
        await _operationLock.WaitAsync();
        try
        {
            if (_uiSettings.Ui.AutoResubscribe)
                await ReconcileToSavedTopicsAsync();

            List<KeyValuePair<string, MqttQualityOfServiceLevel>> snapshot;
            lock (_topicsSync)
            {
                if (_topics.Count == 0) return;
                snapshot = _topics.ToList();
            }

            var filters = snapshot
                .Select(kv => new MqttTopicFilterBuilder()
                    .WithTopic(kv.Key)
                    .WithQualityOfServiceLevel(kv.Value)
                    .Build())
                .ToList();
            await _managedMqttClient.SubscribeAsync(filters);
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Re-subscribed to {Count} topic(s) after connect", snapshot.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to re-subscribe topics after connect");
        }
        finally
        {
            _operationLock.Release();
        }
    }

    // Reconcile in-memory topics to the saved On Connect list: unsubscribe
    // topics removed from saved, add missing saved topics. If UnsubscribeAsync
    // throws, _topics keeps the old keys so a later reconnect can retry.
    private async Task ReconcileToSavedTopicsAsync()
    {
        var connection = _sessionState.SelectedConnection;
        var saved = connection.SubscribedTopics;
        var savedNames = saved.Select(s => s.Topic).ToHashSet(StringComparer.Ordinal);

        List<string> toUnsubscribe;
        List<SubscribedTopic> toAdd;

        lock (_topicsSync)
        {
            toUnsubscribe = _topics.Keys.Where(k => !savedNames.Contains(k)).ToList();
            toAdd = saved.Where(entry => !_topics.ContainsKey(entry.Topic)).ToList();
        }

        if (toUnsubscribe.Count > 0)
            await _managedMqttClient.UnsubscribeAsync(toUnsubscribe);

        lock (_topicsSync)
        {
            foreach (var key in toUnsubscribe)
                _topics.Remove(key);

            foreach (var entry in toAdd)
                _topics[entry.Topic] = entry.QualityOfServiceLevel;
        }
    }

    private async Task PersistTopicsAsync()
    {
        try
        {
            var sessionConn = _sessionState.SelectedConnection;
            List<SubscribedTopic> snapshot;
            lock (_topicsSync)
            {
                snapshot = _topics
                    .Select(kv => new SubscribedTopic
                    {
                        Topic = kv.Key,
                        QualityOfServiceLevel = kv.Value
                    })
                    .ToList();
            }

            // Resolve from stored connections to avoid writing stale profile fields
            // from the connect-time session clone.
            var stored = _connectionSettings.Connections
                .FirstOrDefault(c => c.Id == sessionConn.Id);
            var toSave = (stored ?? sessionConn).Clone();
            toSave.SubscribedTopics = snapshot;

            // Keep session topics in sync so auto-reconnect sees edits.
            sessionConn.SubscribedTopics = snapshot
                .Select(t => new SubscribedTopic { Topic = t.Topic, QualityOfServiceLevel = t.QualityOfServiceLevel })
                .ToList();

            await _connectionSettings.AddConnectionAsync(toSave);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist subscription topics");
        }
    }

    private Task OnSyncFailed(MqttManagedProcessFailedEventArgs args)
    {
        try
        {
            _logger.LogError(args.Exception, "Subscription synchronization failed");
            _notifier.Notify(new UserNotification(
                UserNotificationSeverity.Error,
                "Subscription sync failed — the broker may have rejected the topic. Check broker ACL settings."));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to show subscription sync failure notification");
        }
        return Task.CompletedTask;
    }

    private bool _disposed;

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing)
        {
            _managedMqttClient.ConnectedAsync -= OnConnected;
            _managedMqttClient.SynchronizingSubscriptionsFailedAsync -= OnSyncFailed;
            _operationLock.Dispose();
        }
        _disposed = true;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}
