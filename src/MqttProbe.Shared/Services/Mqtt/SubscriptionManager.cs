using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Protocol;
using MqttProbe.Models.Mqtt;
using MqttProbe.Services.Configuration;
using MudBlazor;

namespace MqttProbe.Services.Mqtt;

public interface ISubscriptionManager : IDisposable
{
    public IReadOnlyList<SubscribedTopic> Subscriptions { get; }
    public Task Remove(List<string> topics);
    public Task Add(string topic, MqttQualityOfServiceLevel qos = MqttQualityOfServiceLevel.AtLeastOnce);
    public void ClearActiveSubscriptions();
}

public class SubscriptionManager : ISubscriptionManager, IDisposable
{
    private readonly IMqttManagedClient _managedMqttClient;
    private readonly ILogger<SubscriptionManager> _logger;
    private readonly ISnackbar _snackbar;
    private readonly ISettingsStore _settingsStore;
    private readonly ISessionState _sessionState;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly Lock _topicsSync = new();
    private readonly Dictionary<string, MqttQualityOfServiceLevel> _topics = new(StringComparer.Ordinal);

    public SubscriptionManager(IMqttManagedClient managedMqttClient, ILogger<SubscriptionManager> logger,
        ISnackbar snackbar, ISettingsStore settingsStore, ISessionState sessionState)
    {
        _managedMqttClient = managedMqttClient;
        _logger = logger;
        _snackbar = snackbar;
        _settingsStore = settingsStore;
        _sessionState = sessionState;
        _managedMqttClient.ConnectedAsync += OnConnected;
        _managedMqttClient.SynchronizingSubscriptionsFailedAsync += OnSyncFailed;
    }

    private const int MaxSubscriptions = 500;

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

    public async Task Remove(List<string> topics)
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
            _snackbar.Add("Invalid topic", Severity.Warning);
            _logger.LogWarning("Rejected invalid subscription topic (length={Len})", topic?.Length ?? 0);
            return;
        }

        await _operationLock.WaitAsync();
        try
        {
            lock (_topicsSync)
            {
                if (_topics.ContainsKey(topic))
                {
                    _snackbar.Add($"Already subscribed to {topic}", Severity.Warning);
                    return;
                }

                if (_topics.Count >= MaxSubscriptions)
                {
                    _snackbar.Add($"Subscription limit ({MaxSubscriptions}) reached", Severity.Warning);
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

            _snackbar.Add($"Subscribed to {topic}", Severity.Success);
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
            if (_settingsStore.Config.Ui.AutoResubscribe)
            {
                var connection = _sessionState.SelectedConnection;
                lock (_topicsSync)
                {
                    foreach (var entry in connection.SubscribedTopics)
                    {
                        if (!_topics.ContainsKey(entry.Topic))
                            _topics[entry.Topic] = entry.QualityOfServiceLevel;
                    }
                }
            }

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

    private async Task PersistTopicsAsync()
    {
        try
        {
            var connection = _sessionState.SelectedConnection;
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
            connection.SubscribedTopics = snapshot;
            await _settingsStore.AddConnectionAsync(connection);
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
            _snackbar.Add(
                "Subscription sync failed — the broker may have rejected the topic. Check broker ACL settings.",
                Severity.Error);
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
