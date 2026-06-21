using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;
using MqttProbe.Models.Mqtt;
using MqttProbe.Services.Configuration;
using MudBlazor;

namespace MqttProbe.Services.Mqtt;

public interface ISubscriptionManager : IDisposable
{
    public IReadOnlySet<string> Topics { get; }
    public Task Remove(List<string> topics);
    public Task Add(string topic);
}

public class SubscriptionManager : ISubscriptionManager, IDisposable
{
    private readonly IManagedMqttClient _managedMqttClient;
    private readonly ILogger<SubscriptionManager> _logger;
    private readonly ISnackbar _snackbar;
    private readonly ISettingsStore _settingsStore;
    private readonly ISessionState _sessionState;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly Lock _topicsSync = new();
    private readonly HashSet<string> _topics = [];

    public SubscriptionManager(IManagedMqttClient managedMqttClient, ILogger<SubscriptionManager> logger,
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

    public IReadOnlySet<string> Topics
    {
        get
        {
            lock (_topicsSync)
                return _topics.ToHashSet();
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

    public async Task Add(string topic)
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
            var isAtLimit = false;
            lock (_topicsSync)
            {
                isAtLimit = _topics.Count >= MaxSubscriptions;
            }

            if (isAtLimit)
            {
                _snackbar.Add($"Subscription limit ({MaxSubscriptions}) reached", Severity.Warning);
                _logger.LogWarning("Subscription limit ({Limit}) reached; topic {Topic} not added",
                    MaxSubscriptions, topic);
                return;
            }

            var topicFilter = new MqttTopicFilterBuilder()
                .WithTopic(topic)
                .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();

            await _managedMqttClient.SubscribeAsync([topicFilter]);

            lock (_topicsSync)
            {
                _topics.Add(topic);
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

    private async Task OnConnected(MqttClientConnectedEventArgs args)
    {
        await _operationLock.WaitAsync();
        try
        {
            // Load saved topics from the connection profile if auto-resubscribe is on.
            if (_settingsStore.Config.Ui.AutoResubscribe)
            {
                var connection = _sessionState.SelectedConnection;
                lock (_topicsSync)
                {
                    foreach (var topic in connection.SubscribedTopics)
                        _topics.Add(topic);
                }
            }

            HashSet<string> topics;
            lock (_topicsSync)
            {
                if (_topics.Count == 0) return;
                topics = _topics.ToHashSet();
            }

            var filters = topics
                .Select(t => new MqttTopicFilterBuilder()
                    .WithTopic(t)
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build())
                .ToList();
            await _managedMqttClient.SubscribeAsync(filters);
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Re-subscribed to {Count} topic(s) after connect", topics.Count);
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
            List<string> topics;
            lock (_topicsSync)
            {
                topics = [.. _topics];
            }
            connection.SubscribedTopics = topics;
            await _settingsStore.AddConnectionAsync(connection);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist subscription topics");
        }
    }

    private Task OnSyncFailed(ManagedProcessFailedEventArgs args)
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
