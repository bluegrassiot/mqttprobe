using Microsoft.Extensions.Logging;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Plugins.Protobuf;

namespace MqttProbe.Core.Services.Mqtt;

public interface ITopicExcludeService : IDisposable
{
    public IReadOnlyList<string> TopicExcludes { get; }
    public TopicExcludeValidationResult ValidateAdd(string topic);
    public bool IsExcluded(string topic);
    public Task<TopicExcludeOperationResult> Add(string topic);
    public Task<TopicExcludeOperationResult> Remove(IReadOnlyList<string> topics);
    public void ClearActiveTopicExcludes();
    public event Action<string>? TopicExcluded;
}

public sealed record TopicExcludeValidationResult(bool IsValid, UserNotification? Feedback = null);
public sealed record TopicExcludeOperationResult(bool IsValid, UserNotification? Feedback = null);

public sealed class TopicExcludeService : ITopicExcludeService
{
    private const int MaxTopicExcludes = 500;

    private readonly ILogger<TopicExcludeService> _logger;
    private readonly IConnectionSettings _connectionSettings;
    private readonly ISessionState _sessionState;
    private readonly IMqttManagedClient? _managedMqttClient;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly Lock _topicsSync = new();
    private readonly HashSet<string> _topics = new(StringComparer.Ordinal);
    private long _connectionGeneration;
    private bool _disposed;

    public event Action<string>? TopicExcluded;

    public TopicExcludeService(ILogger<TopicExcludeService> logger,
        IConnectionSettings connectionSettings, ISessionState sessionState,
        IMqttManagedClient? managedMqttClient = null)
    {
        _logger = logger;
        _connectionSettings = connectionSettings;
        _sessionState = sessionState;
        _managedMqttClient = managedMqttClient;
        _sessionState.SelectedConnectionChanged += OnSelectedConnectionChanged;
        if (_managedMqttClient is not null)
            _managedMqttClient.ConnectedAsync += OnConnected;
        LoadFromConnection(_sessionState.SelectedConnection);
    }

    public IReadOnlyList<string> TopicExcludes
    {
        get
        {
            lock (_topicsSync)
                return [.. _topics];
        }
    }

    public bool IsExcluded(string topic)
    {
        lock (_topicsSync)
            return _topics.Any(filter => Matches(topic, filter));
    }

    public static bool Matches(string topic, string filter) => string.Equals(filter, "#", StringComparison.Ordinal) || MqttTopicMatcher.Matches(topic, filter);

    public TopicExcludeValidationResult ValidateAdd(string topic)
    {
        if (!IsValid(topic))
            return Invalid("Invalid topic exclusion");

        lock (_topicsSync)
            return ValidateAddLocked(topic);
    }

    public async Task<TopicExcludeOperationResult> Add(string topic)
    {
        var initialValidation = ValidateAdd(topic);
        if (!initialValidation.IsValid)
            return new(false, initialValidation.Feedback);

        await _operationLock.WaitAsync();
        try
        {
            Connection activeConnection;
            long generation;
            string[] candidate;
            lock (_topicsSync)
            {
                var validation = ValidateAddLocked(topic);
                if (!validation.IsValid)
                    return new(false, validation.Feedback);

                activeConnection = _sessionState.SelectedConnection;
                generation = _connectionGeneration;
                candidate = [.. _topics, topic];
            }

            var persistenceError = await PersistTopicsAsync(activeConnection, candidate);
            if (persistenceError is not null)
                return PersistenceFailure();

            lock (_topicsSync)
            {
                if (!IsCurrentConnection(activeConnection, generation))
                    return ConnectionChangedFailure();
                _topics.Add(topic);
                TopicExcluded?.Invoke(topic);
            }
            return new(true);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<TopicExcludeOperationResult> Remove(IReadOnlyList<string> topics)
    {
        await _operationLock.WaitAsync();
        try
        {
            Connection activeConnection;
            string[] candidate;
            long generation;
            lock (_topicsSync)
            {
                activeConnection = _sessionState.SelectedConnection;
                generation = _connectionGeneration;
                candidate = [.. _topics.Where(topic => !topics.Contains(topic, StringComparer.Ordinal))];
            }

            var persistenceError = await PersistTopicsAsync(activeConnection, candidate);
            if (persistenceError is not null)
                return PersistenceFailure();

            lock (_topicsSync)
            {
                if (!IsCurrentConnection(activeConnection, generation))
                    return ConnectionChangedFailure();
                _topics.Clear();
                foreach (var topic in candidate)
                    _topics.Add(topic);
            }
            return new(true);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public void ClearActiveTopicExcludes()
    {
        lock (_topicsSync)
        {
            _connectionGeneration++;
            _topics.Clear();
        }
    }

    private static bool IsValid(string topic) => MqttTopicMatcher.IsValidFilter(topic);

    private void OnSelectedConnectionChanged(Connection connection) => LoadFromConnection(connection);

    private Task OnConnected(MQTTnet.MqttClientConnectedEventArgs args)
    {
        LoadFromConnection(_sessionState.SelectedConnection);
        return Task.CompletedTask;
    }

    private void LoadFromConnection(Connection connection)
    {
        lock (_topicsSync)
        {
            _connectionGeneration++;
            _topics.Clear();
            foreach (var topic in (connection.TopicExcludes).Where(IsValid).Take(MaxTopicExcludes))
                _topics.Add(topic);
            foreach (var topic in _topics)
                TopicExcluded?.Invoke(topic);
        }
    }

    private async Task<Exception?> PersistTopicsAsync(Connection sessionConn, IReadOnlyList<string> topics)
    {
        try
        {
            var stored = _connectionSettings.Connections.FirstOrDefault(c => c.Id == sessionConn.Id);
            var toSave = (stored ?? sessionConn).Clone();
            toSave.TopicExcludes = [.. topics];
            await _connectionSettings.AddConnectionAsync(toSave);
            sessionConn.TopicExcludes = [.. topics];
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist topic exclusions");
            return ex;
        }
    }

    private static TopicExcludeValidationResult Invalid(string message) =>
        new(false, new UserNotification(UserNotificationSeverity.Warning, message));

    private TopicExcludeValidationResult ValidateAddLocked(string topic)
    {
        if (_topics.Contains(topic))
            return Invalid($"Already excluding {topic}");

        if (_topics.Count >= MaxTopicExcludes)
            return Invalid($"Topic exclusion limit ({MaxTopicExcludes}) reached");

        return new(true);
    }

    private static TopicExcludeOperationResult PersistenceFailure() =>
        new(false, new UserNotification(UserNotificationSeverity.Error,
            "Failed to save topic exclusions"));

    private static TopicExcludeOperationResult ConnectionChangedFailure() =>
        new(false, new UserNotification(UserNotificationSeverity.Warning,
            "Active connection changed; topic exclusion was not applied"));

    private bool IsCurrentConnection(Connection connection, long generation) =>
        generation == _connectionGeneration && ReferenceEquals(_sessionState.SelectedConnection, connection);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sessionState.SelectedConnectionChanged -= OnSelectedConnectionChanged;
        if (_managedMqttClient is not null)
            _managedMqttClient.ConnectedAsync -= OnConnected;
        _operationLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
