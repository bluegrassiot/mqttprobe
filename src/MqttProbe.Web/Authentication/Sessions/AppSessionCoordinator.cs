using System.Security.Cryptography;

namespace MqttProbe.Web.Authentication;

public sealed class AppSessionCoordinator : IDisposable
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, AppSessionRecord> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<IReadOnlyList<CircuitLease>>> _pendingRevocations = new(StringComparer.Ordinal);
    private readonly HashSet<string> _forceLoginSuppressed = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _sessionLifetime;

    public AppSessionCoordinator(TimeProvider timeProvider, TimeSpan sessionLifetime)
    {
        _timeProvider = timeProvider;
        _sessionLifetime = sessionLifetime;
    }

    public event Action<AppSessionRecord>? SessionRevoked;

    public AppSessionRecord CreateSession(ExternalIdentity identity)
    {
        var sessionId = GenerateSessionId();
        var expiresAt = _timeProvider.GetUtcNow().Add(_sessionLifetime);

        var record = new AppSessionRecord(sessionId, identity, expiresAt);

        lock (_lock)
        {
            _sessions[sessionId] = record;
        }

        ScheduleExpiryTimer(record);

        return record;
    }

    public AppSessionRecord? GetSession(string sessionId)
    {
        lock (_lock)
        {
            return _sessions.TryGetValue(sessionId, out var record) ? record : null;
        }
    }

    public CircuitLease? ValidateAndRegister(
        string sessionId,
        string issuer,
        string subject,
        CircuitLease lease)
    {
        AppSessionRecord? record;
        lock (_lock)
        {
            _sessions.TryGetValue(sessionId, out record);
        }

        if (record is null)
        {
            return null;
        }

        if (!record.IsValid(sessionId, issuer, subject, _timeProvider.GetUtcNow()))
        {
            return null;
        }

        return record.RegisterCircuit(lease);
    }

    public void UnregisterCircuit(string sessionId, string circuitId)
    {
        AppSessionRecord? record;
        lock (_lock)
        {
            _sessions.TryGetValue(sessionId, out record);
        }

        record?.UnregisterCircuit(circuitId);
    }

    public AppSessionRecord? FindSessionByCircuitId(string circuitId)
    {
        lock (_lock)
        {
            return _sessions.Values.FirstOrDefault(s => s.ContainsCircuit(circuitId));
        }
    }

    // Single idempotent async revocation for logout and timer expiry.
    // Convenience overload: revocations other than explicit logout keep the
    // force-login navigation.
    public Task<IReadOnlyList<CircuitLease>> RevokeSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
        => RevokeSessionAsync(sessionId, notifyForceLogin: true, cancellationToken);

    // Suppression is recorded on the session rather than copied into the
    // teardown call, so it still applies when an expiry/security revocation
    // for the same session is already tearing down.
    public Task<IReadOnlyList<CircuitLease>> RevokeSessionAsync(
        string sessionId,
        bool notifyForceLogin,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            // Only latch while the session exists: that guarantees the core
            // cleanup below removes it, and a teardown already past its
            // notification step has nothing left to suppress.
            if (!notifyForceLogin && _sessions.ContainsKey(sessionId))
            {
                _forceLoginSuppressed.Add(sessionId);
            }

            if (_pendingRevocations.TryGetValue(sessionId, out var existing))
            {
                return existing;
            }

            if (!_sessions.TryGetValue(sessionId, out var record))
            {
                return Task.FromResult<IReadOnlyList<CircuitLease>>([]);
            }

            if (record.State == SessionState.Revoked)
            {
                return Task.FromResult<IReadOnlyList<CircuitLease>>([]);
            }

            // Register the pending task BEFORE starting core work so that
            // synchronous completions (zero-circuit sessions) find the entry
            // when their cleanup runs.
            var tcs = new TaskCompletionSource<IReadOnlyList<CircuitLease>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingRevocations[sessionId] = tcs.Task;
            _ = RevokeSessionCoreAsync(sessionId, record, tcs, cancellationToken);
            return tcs.Task;
        }
    }

    private async Task RevokeSessionCoreAsync(
        string sessionId,
        AppSessionRecord record,
        TaskCompletionSource<IReadOnlyList<CircuitLease>> tcs,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<CircuitLease> leases = [];
        Exception? fault = null;

        try
        {
            ITimer? expiryTimer;

            lock (_lock)
            {
                _expiryTimers.TryGetValue(sessionId, out expiryTimer);
                _expiryTimers.Remove(sessionId);
            }

            // Atomic transition + gate snapshot
            leases = record.Revoke();

            expiryTimer?.Dispose();

            // Fire event (exceptions cannot skip removal)
            try
            {
                SessionRevoked?.Invoke(record);
            }
            catch
            {
                // Event exceptions cannot prevent session removal
            }

            await TearDownLeasesAsync(
                leases,
                () => !IsForceLoginSuppressed(sessionId),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            fault = ex;
        }

        // Registry cleanup under lock BEFORE signaling the TCS, so that any
        // caller resuming from await sees the dictionaries already updated.
        lock (_lock)
        {
            _sessions.Remove(sessionId);
            _pendingRevocations.Remove(sessionId);
            _forceLoginSuppressed.Remove(sessionId);
        }

        // Signal TCS outside the lock to avoid deadlocks with continuations.
        if (fault is not null)
        {
            tcs.TrySetException(fault);
        }
        else
        {
            tcs.TrySetResult(leases);
        }
    }

    private bool IsForceLoginSuppressed(string sessionId)
    {
        lock (_lock)
        {
            return _forceLoginSuppressed.Contains(sessionId);
        }
    }

    private static async Task TearDownLeasesAsync(
        IReadOnlyList<CircuitLease> leases,
        Func<bool> shouldNotifyForceLogin,
        CancellationToken cancellationToken)
    {
        if (leases.Count == 0)
        {
            return;
        }

        try
        {
            var teardownTasks = leases.Select(l => l.TeardownAsync(shouldNotifyForceLogin, cancellationToken));
            await Task.WhenAll(teardownTasks).ConfigureAwait(false);
        }
        catch
        {
            // Cleanup exceptions cannot prevent session removal
        }
    }

    public void RemoveSession(string sessionId)
    {
        lock (_lock)
        {
            _sessions.Remove(sessionId);
            _forceLoginSuppressed.Remove(sessionId);
        }
    }

    private readonly Dictionary<string, ITimer> _expiryTimers = new(StringComparer.Ordinal);

    private void ScheduleExpiryTimer(AppSessionRecord record)
    {
        var delay = record.ExpiresAt - _timeProvider.GetUtcNow();
        if (delay <= TimeSpan.Zero)
        {
            _ = RevokeSessionAsync(record.SessionId);
            return;
        }

        var timer = _timeProvider.CreateTimer(
            _ => OnSessionExpired(record.SessionId),
            null,
            delay,
            Timeout.InfiniteTimeSpan);

        lock (_lock)
        {
            _expiryTimers[record.SessionId] = timer;
        }
    }

    private void OnSessionExpired(string sessionId)
    {
        // Fire and forget - the task is stored in _pendingRevocations
        _ = RevokeSessionAsync(sessionId);
    }

    private static string GenerateSessionId()
    {
        var bytes = RandomNumberGenerator.GetBytes(24);
        return Convert.ToBase64String(bytes);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var timer in _expiryTimers.Values)
            {
                timer.Dispose();
            }
            _expiryTimers.Clear();
        }
    }
}
