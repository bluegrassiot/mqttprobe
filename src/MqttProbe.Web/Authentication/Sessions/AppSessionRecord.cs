namespace MqttProbe.Web.Authentication;

public sealed class AppSessionRecord
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, CircuitLease> _leases = new(StringComparer.Ordinal);
    private SessionState _state = SessionState.Active;

    internal AppSessionRecord(
        string sessionId,
        ExternalIdentity identity,
        DateTimeOffset expiresAt,
        string? sid)
    {
        SessionId = sessionId;
        Identity = identity;
        ExpiresAt = expiresAt;
        Sid = sid;
    }

    public string SessionId { get; }
    public ExternalIdentity Identity { get; }
    public DateTimeOffset ExpiresAt { get; }

    // sid from the ID token at login; null when the token carried none.
    public string? Sid { get; }

    public string Issuer => Identity.Issuer;
    public string Subject => Identity.Subject;
    public string DisplayName => Identity.DisplayName;

    public SessionState State
    {
        get { lock (_lock) return _state; }
    }

    public int CircuitCount
    {
        get { lock (_lock) return _leases.Count; }
    }

    // Atomic validation: session ID, issuer, subject, Active state, now < expiry.
    public bool IsValid(string sessionId, string issuer, string subject, DateTimeOffset now)
    {
        lock (_lock)
        {
            return _state == SessionState.Active
                && string.Equals(SessionId, sessionId, StringComparison.Ordinal)
                && string.Equals(Identity.Issuer, issuer, StringComparison.Ordinal)
                && string.Equals(Identity.Subject, subject, StringComparison.Ordinal)
                && now < ExpiresAt;
        }
    }

    // A logout token naming a sid only matches the session holding that sid, so
    // a foreign sid can never revoke this session through its subject alone.
    public bool MatchesBackChannelLogout(string issuer, string? sid, string? subject)
    {
        lock (_lock)
        {
            if (!string.Equals(Identity.Issuer, issuer, StringComparison.Ordinal))
            {
                return false;
            }

            if (sid is not null)
            {
                return string.Equals(Sid, sid, StringComparison.Ordinal) &&
                    (subject is null || string.Equals(Identity.Subject, subject, StringComparison.Ordinal));
            }

            return subject is not null &&
                string.Equals(Identity.Subject, subject, StringComparison.Ordinal);
        }
    }

    internal CircuitLease? RegisterCircuit(CircuitLease lease)
    {
        lock (_lock)
        {
            if (_state != SessionState.Active)
            {
                return null;
            }

            if (!lease.IsBound)
            {
                return null;
            }

            if (_leases.TryGetValue(lease.CircuitId, out var existing))
            {
                return existing;
            }

            _leases[lease.CircuitId] = lease;
            return lease;
        }
    }

    internal void UnregisterCircuit(string circuitId)
    {
        lock (_lock)
        {
            _leases.Remove(circuitId);
        }
    }

    internal bool ContainsCircuit(string circuitId)
    {
        lock (_lock)
        {
            return _leases.ContainsKey(circuitId);
        }
    }

    public CircuitLease? GetLease(string circuitId)
    {
        lock (_lock)
        {
            return _leases.TryGetValue(circuitId, out var lease) ? lease : null;
        }
    }

    // Secure invalidation: revoked state with no gate left able to admit
    // activity. A revocation that left one gate open must not be reported as
    // success.
    public bool AreGatesRevoked
    {
        get
        {
            lock (_lock)
            {
                return _state == SessionState.Revoked
                    && _leases.Values.All(lease => !lease.Gate.IsActive);
            }
        }
    }

    // Transitions Active→Revoking→Revoked atomically and revokes every lease
    // gate, so AreGatesRevoked can never read true while a gate can still admit
    // activity. Revocation callbacks never run here: they are owned by the gate.
    // Idempotent: subsequent calls return an empty list.
    internal IReadOnlyList<CircuitLease> Revoke()
    {
        lock (_lock)
        {
            if (_state == SessionState.Revoked)
            {
                return [];
            }

            _state = SessionState.Revoking;

            var activeLeases = _leases.Values.ToList();

            foreach (var lease in activeLeases)
            {
                try
                {
                    lease.Gate.Revoke();
                }
                catch (Exception)
                {
                    // One gate failing to start revoking must not leave its
                    // siblings open or skip their teardown below. A gate that
                    // stayed open is caught by AreGatesRevoked, which turns it
                    // into a revocation failure instead of a silent success.
                }
            }

            _state = SessionState.Revoked;
            return activeLeases;
        }
    }
}
