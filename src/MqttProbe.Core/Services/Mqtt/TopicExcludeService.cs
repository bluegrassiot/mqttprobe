using Microsoft.Extensions.Logging;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Plugins.Protobuf;

namespace MqttProbe.Core.Services.Mqtt;

public sealed partial class TopicExcludeService : ITopicExcludeService
{
    private const int MaxTopicExcludes = 500;

    private readonly ILogger<TopicExcludeService> _logger;
    private readonly IConnectionSettings _connectionSettings;
    private readonly ISessionState _sessionState;
    private readonly IMqttManagedClient? _managedMqttClient;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly Lock _sync = new();
    private readonly HashSet<string> _topics = new(StringComparer.Ordinal);
    private readonly List<SparkplugTopicExclude> _sparkplugRules = [];
    private readonly Dictionary<string, int> _inFlightReaders = new(StringComparer.Ordinal);
    private readonly List<PendingPurge> _pendingPurges = [];
    private long _connectionGeneration;
    private Connection? _loadedConnection;
    private bool _disposed;

    public event Action<string>? PurgeExcludedTopic;
    public event Action<string>? TopicExcluded;
    public event Action<SparkplugTopicExclude>? PurgeExcludedSparkplug;
    public event Action<SparkplugTopicExclude>? SparkplugExcluded;

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
            lock (_sync)
                return [.. _topics];
        }
    }

    public IReadOnlyList<SparkplugTopicExclude> SparkplugExcludes
    {
        get
        {
            lock (_sync)
                return [.. _sparkplugRules];
        }
    }

    public bool IsExcluded(string topic)
    {
        lock (_sync)
            return IsExcludedLocked(topic);
    }

    public IDisposable? TryEnter(string topic)
    {
        lock (_sync)
        {
            if (IsExcludedLocked(topic))
                return null;

            _inFlightReaders[topic] = _inFlightReaders.GetValueOrDefault(topic) + 1;
            return new ReadGate(this, topic);
        }
    }

    private bool IsExcludedLocked(string topic) =>
        _topics.Any(filter => Matches(topic, filter))
        || _sparkplugRules.Any(rule => TopicBranchMatcher.MatchesSparkplug(rule.Scope, rule.MessageType, topic));

    public static bool Matches(string topic, string filter) => string.Equals(filter, "#", StringComparison.Ordinal) || MqttTopicMatcher.Matches(topic, filter);

    public TopicExcludeValidationResult ValidateAdd(string topic)
    {
        if (!IsValid(topic))
            return Invalid("Invalid topic exclusion");

        lock (_sync)
            return ValidateAddLocked(topic);
    }

    public async Task<TopicExcludeOperationResult> Add(string topic)
    {
        var (outcome, feedback) = await AddCore(topic, boundConnection: null, boundGeneration: 0);
        return new(outcome == TopicExcludeAddOutcome.Added, feedback);
    }

    public async Task<TopicExcludeOperationResult> Remove(IReadOnlyList<string> topics)
    {
        if (!await EnterOperationAsync())
            return ServiceClosedFailure();
        try
        {
            Connection activeConnection;
            string[] candidate;
            SparkplugTopicExclude[] candidateRules;
            long generation;
            lock (_sync)
            {
                activeConnection = _sessionState.SelectedConnection;
                generation = _connectionGeneration;
                candidate = [.. _topics.Where(topic => !topics.Contains(topic, StringComparer.Ordinal))];
                candidateRules = [.. _sparkplugRules];
            }

            var persistenceError = await PersistTopicsAsync(activeConnection, candidate, candidateRules);
            if (persistenceError is not null)
                return PersistenceFailure();

            lock (_sync)
            {
                if (_disposed)
                    return ServiceClosedFailure();

                if (!IsCurrentConnectionLocked(activeConnection, generation))
                    return ConnectionChangedFailure();
                _topics.Clear();
                foreach (var topic in candidate)
                    _topics.Add(topic);
                _sparkplugRules.Clear();
                _sparkplugRules.AddRange(candidateRules);

                // Remove can overtake a purge parked on an in-flight reader; completing it
                // here gives Add its outcome now instead of after an unrelated reader ends.
                DropRemovedPendingPurgesLocked();
            }
            return new(true);
        }
        finally
        {
            ReleaseOperationLock();
        }
    }

    public void ClearActiveTopicExcludes()
    {
        lock (_sync)
        {
            _connectionGeneration++;
            DropPendingPurgesLocked();
            _topics.Clear();
            _sparkplugRules.Clear();
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
        List<PendingPurge> purges = [];
        lock (_sync)
        {
            _connectionGeneration++;
            _loadedConnection = connection;
            DropPendingPurgesLocked();
            _topics.Clear();
            _sparkplugRules.Clear();
            foreach (var topic in (connection.TopicExcludes).Where(IsValid).Take(MaxTopicExcludes))
                _topics.Add(topic);
            foreach (var rule in connection.SparkplugTopicExcludes)
            {
                if (_topics.Count + _sparkplugRules.Count >= MaxTopicExcludes)
                    break;
                if (!IsValidSparkplugExclude(rule) || _sparkplugRules.Contains(rule))
                    continue;
                _sparkplugRules.Add(rule);
            }

            foreach (var topic in _topics)
            {
                var purge = RequestPurgeLocked(topic, _connectionGeneration, out var deferred);
                if (!deferred)
                    purges.Add(purge);
            }

            foreach (var rule in _sparkplugRules)
            {
                var purge = RequestPurgeLocked(rule, _connectionGeneration, out var deferred);
                if (!deferred)
                    purges.Add(purge);
            }
        }

        // A purger or observer throw on one filter must not abort the remaining ones.
        foreach (var purge in purges)
            FinishPurge(purge);
    }

    // A reader admitted before the filter went live can still write, so the purge is parked
    // until the matching readers leave; a superseded generation drops it instead.
    private PendingPurge RequestPurgeLocked(string filter, long generation, out bool deferred) =>
        RequestPurgeLocked(PendingPurge.ForFilter(filter, generation), out deferred);

    private PendingPurge RequestPurgeLocked(
        SparkplugTopicExclude rule, long generation, out bool deferred) =>
        RequestPurgeLocked(PendingPurge.ForSparkplug(rule, generation), out deferred);

    private PendingPurge RequestPurgeLocked(PendingPurge purge, out bool deferred)
    {
        if (HasMatchingReaderLocked(purge))
        {
            _pendingPurges.Add(purge);
            deferred = true;
            return purge;
        }

        RunPurgersLocked(purge);
        deferred = false;
        return purge;
    }

    // The operation semaphore is never disposed: a queued operation has to be able to enter
    // after the owner releases, then reject on _disposed. Disposing it here would strand every
    // waiter behind an owner whose release no longer wakes them.
    private async Task<bool> EnterOperationAsync()
    {
        await _operationLock.WaitAsync();
        lock (_sync)
        {
            if (!_disposed)
                return true;
        }

        _operationLock.Release();
        return false;
    }

    private void ReleaseOperationLock() => _operationLock.Release();

    private async Task<Exception?> PersistTopicsAsync(
        Connection sessionConn, IReadOnlyList<string> topics, IReadOnlyList<SparkplugTopicExclude> rules)
    {
        try
        {
            var stored = _connectionSettings.Connections.FirstOrDefault(c => c.Id == sessionConn.Id);
            var toSave = (stored ?? sessionConn).Clone();
            toSave.TopicExcludes = [.. topics];
            toSave.SparkplugTopicExcludes = [.. rules];
            await _connectionSettings.AddConnectionAsync(toSave);
            sessionConn.TopicExcludes = [.. topics];
            sessionConn.SparkplugTopicExcludes = [.. rules];
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

        if (_topics.Count + _sparkplugRules.Count >= MaxTopicExcludes)
            return Invalid($"Topic exclusion limit ({MaxTopicExcludes}) reached");

        return new(true);
    }

    private static TopicExcludeOperationResult PersistenceFailure() =>
        new(false, new UserNotification(UserNotificationSeverity.Error,
            "Failed to save topic exclusions"));

    private static TopicExcludeOperationResult ConnectionChangedFailure() =>
        new(false, new UserNotification(UserNotificationSeverity.Warning,
            "Active connection changed; topic exclusion was not applied"));

    private static TopicExcludeOperationResult SavedButNotAppliedFailure() =>
        new(false, new UserNotification(UserNotificationSeverity.Warning,
            "Connection changed while saving; the exclusion was saved but is not applied to the active connection"));

    private static TopicExcludeOperationResult PurgeFailure() =>
        new(false, new UserNotification(UserNotificationSeverity.Error,
            "Exclusion applied and future messages are now blocked, but clearing existing topic data failed"));

    private static TopicExcludeOperationResult ExclusionRemovedFailure() =>
        new(false, new UserNotification(UserNotificationSeverity.Warning,
            "Topic exclusion was removed before its existing data was cleared"));

    private static TopicExcludeOperationResult ServiceClosedFailure() =>
        new(false, new UserNotification(UserNotificationSeverity.Warning,
            "Topic exclusion service is closed; the change was not applied"));

    private bool IsCurrentConnectionLocked(Connection connection, long generation) =>
        generation == _connectionGeneration && ReferenceEquals(_sessionState.SelectedConnection, connection);

    private sealed class ReadGate(TopicExcludeService service, string topic) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;

            service.ReleaseRead(topic);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            // Set under _sync: queued operations check it on entry, Add and Remove read it
            // there once persistence completes.
            _disposed = true;
            DropPendingPurgesLocked();
        }

        _sessionState.SelectedConnectionChanged -= OnSelectedConnectionChanged;
        if (_managedMqttClient is not null)
            _managedMqttClient.ConnectedAsync -= OnConnected;
        // Left undisposed on purpose: waiters must still be able to enter and see _disposed.
        GC.SuppressFinalize(this);
    }
}
