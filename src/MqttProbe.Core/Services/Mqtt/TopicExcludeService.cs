using Microsoft.Extensions.Logging;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Plugins.Protobuf;

namespace MqttProbe.Core.Services.Mqtt;

public sealed class TopicExcludeService : ITopicExcludeService
{
    private const int MaxTopicExcludes = 500;

    private readonly ILogger<TopicExcludeService> _logger;
    private readonly IConnectionSettings _connectionSettings;
    private readonly ISessionState _sessionState;
    private readonly IMqttManagedClient? _managedMqttClient;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly Lock _sync = new();
    private readonly HashSet<string> _topics = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _inFlightReaders = new(StringComparer.Ordinal);
    private readonly List<PendingPurge> _pendingPurges = [];
    private long _connectionGeneration;
    private bool _disposed;

    public event Action<string>? PurgeExcludedTopic;
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
            lock (_sync)
                return [.. _topics];
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
        _topics.Any(filter => Matches(topic, filter));

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
        var initialValidation = ValidateAdd(topic);
        if (!initialValidation.IsValid)
            return new(false, initialValidation.Feedback);

        PendingPurge? purge = null;
        var deferred = false;
        if (!await EnterOperationAsync())
            return ServiceClosedFailure();
        try
        {
            Connection activeConnection;
            long generation;
            string[] candidate;
            lock (_sync)
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

            lock (_sync)
            {
                // Disposal can land while persistence is awaited. The change stays on the
                // connection (it loads with the next service), but this call must not claim
                // an active filter it can no longer purge or back.
                if (_disposed)
                    return ServiceClosedFailure();

                if (!IsCurrentConnectionLocked(activeConnection, generation))
                    return ConnectionChangedFailure();

                _topics.Add(topic);
                purge = RequestPurgeLocked(topic, generation, out deferred);
            }
        }
        finally
        {
            ReleaseOperationLock();
        }

        if (purge is null)
            return ConnectionChangedFailure();

        return await SettlePurgeAsync(purge, deferred);
    }

    // Runs with neither the operation semaphore nor _sync held, so a subscriber that
    // re-enters Add from a notification cannot deadlock against this call.
    private async Task<TopicExcludeOperationResult> SettlePurgeAsync(PendingPurge purge, bool deferred)
    {
        var outcome = deferred ? await purge.Completion.Task : FinishPurge(purge);
        return outcome switch
        {
            PurgeOutcome.Completed => new(true),
            PurgeOutcome.Failed => PurgeFailure(),
            PurgeOutcome.Removed => ExclusionRemovedFailure(),
            _ => ConnectionChangedFailure()
        };
    }

    public async Task<TopicExcludeOperationResult> Remove(IReadOnlyList<string> topics)
    {
        if (!await EnterOperationAsync())
            return ServiceClosedFailure();
        try
        {
            Connection activeConnection;
            string[] candidate;
            long generation;
            lock (_sync)
            {
                activeConnection = _sessionState.SelectedConnection;
                generation = _connectionGeneration;
                candidate = [.. _topics.Where(topic => !topics.Contains(topic, StringComparer.Ordinal))];
            }

            var persistenceError = await PersistTopicsAsync(activeConnection, candidate);
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
            DropPendingPurgesLocked();
            _topics.Clear();
            foreach (var topic in (connection.TopicExcludes).Where(IsValid).Take(MaxTopicExcludes))
                _topics.Add(topic);
            foreach (var topic in _topics)
            {
                var purge = RequestPurgeLocked(topic, _connectionGeneration, out var deferred);
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
    private PendingPurge RequestPurgeLocked(string filter, long generation, out bool deferred)
    {
        var purge = new PendingPurge(filter, generation);
        if (HasMatchingReaderLocked(filter))
        {
            _pendingPurges.Add(purge);
            deferred = true;
            return purge;
        }

        RunPurgersLocked(purge);
        deferred = false;
        return purge;
    }

    // Purgers always run with _sync held; only a purge running inline with Add or Remove
    // also holds the operation semaphore, never a purge finished later (reader release,
    // connection load). They may mutate state but must not notify observers: a subscriber
    // re-entering Add from here deadlocks against the lock or the semaphore still held.
    private void RunPurgersLocked(PendingPurge purge)
    {
        if (PurgeExcludedTopic is not { } purgers)
            return;

        foreach (var purger in purgers.GetInvocationList().Cast<Action<string>>())
        {
            try
            {
                purger(purge.Filter);
            }
            catch (Exception ex)
            {
                purge.Failed = true;
                _logger.LogError(ex, "Failed to purge excluded topic data for {Filter}", purge.Filter);
            }
        }
    }

    private PurgeOutcome FinishPurge(PendingPurge purge)
    {
        var outcome = purge.Failed ? PurgeOutcome.Failed : PurgeOutcome.Completed;
        try
        {
            if (TopicExcluded is { } observers)
            {
                foreach (var observer in observers.GetInvocationList().Cast<Action<string>>())
                {
                    try
                    {
                        observer(purge.Filter);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Topic exclusion observer failed for {Filter}", purge.Filter);
                    }
                }
            }
        }
        finally
        {
            purge.Completion.TrySetResult(outcome);
        }

        return outcome;
    }

    private bool HasMatchingReaderLocked(string filter) =>
        _inFlightReaders.Keys.Any(topic => Matches(topic, filter));

    private void DropPendingPurgesLocked()
    {
        foreach (var purge in _pendingPurges)
            purge.Completion.TrySetResult(PurgeOutcome.Superseded);

        _pendingPurges.Clear();
    }

    // A parked purge whose filter is gone no longer has anything to clear, so it settles
    // here with the outcome the caller can act on instead of waiting for a reader release.
    private void DropRemovedPendingPurgesLocked()
    {
        for (var i = _pendingPurges.Count - 1; i >= 0; i--)
        {
            var purge = _pendingPurges[i];
            if (_topics.Contains(purge.Filter))
                continue;

            _pendingPurges.RemoveAt(i);
            purge.Completion.TrySetResult(PurgeOutcome.Removed);
        }
    }

    // Parked purges publish here: the write they were waiting for has now happened.
    private void ReleaseRead(string topic)
    {
        List<PendingPurge>? purges = null;
        lock (_sync)
        {
            if (_inFlightReaders.TryGetValue(topic, out var readers))
            {
                if (readers > 1)
                    _inFlightReaders[topic] = readers - 1;
                else
                    _inFlightReaders.Remove(topic);
            }

            for (var i = _pendingPurges.Count - 1; i >= 0; i--)
            {
                var purge = _pendingPurges[i];
                if (purge.Generation != _connectionGeneration || !_topics.Contains(purge.Filter))
                {
                    _pendingPurges.RemoveAt(i);
                    purge.Completion.TrySetResult(PurgeOutcome.Superseded);
                }
                else if (!HasMatchingReaderLocked(purge.Filter))
                {
                    _pendingPurges.RemoveAt(i);
                    RunPurgersLocked(purge);
                    (purges ??= []).Add(purge);
                }
            }
        }

        if (purges is null)
            return;

        foreach (var purge in purges)
            FinishPurge(purge);
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

    private static TopicExcludeOperationResult PurgeFailure() =>
        new(false, new UserNotification(UserNotificationSeverity.Error,
            "Exclusion applied, but clearing existing topic data failed"));

    private static TopicExcludeOperationResult ExclusionRemovedFailure() =>
        new(false, new UserNotification(UserNotificationSeverity.Warning,
            "Topic exclusion was removed before its existing data was cleared"));

    private static TopicExcludeOperationResult ServiceClosedFailure() =>
        new(false, new UserNotification(UserNotificationSeverity.Warning,
            "Topic exclusion service is closed; the change was not applied"));

    private bool IsCurrentConnectionLocked(Connection connection, long generation) =>
        generation == _connectionGeneration && ReferenceEquals(_sessionState.SelectedConnection, connection);

    private enum PurgeOutcome
    {
        Superseded,
        Completed,
        Failed,

        // The filter was removed again before its purge could run.
        Removed
    }

    private sealed class PendingPurge(string filter, long generation)
    {
        public string Filter { get; } = filter;
        public long Generation { get; } = generation;
        public bool Failed { get; set; }
        public TaskCompletionSource<PurgeOutcome> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

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
