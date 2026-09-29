namespace MqttProbe.Web.Authentication;

public sealed class AppSessionRecord
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, CircuitLease> _leases = new(StringComparer.Ordinal);
    private SessionState _state = SessionState.Active;

    internal AppSessionRecord(
        string sessionId,
        ExternalIdentity identity,
        DateTimeOffset expiresAt)
    {
        SessionId = sessionId;
        Identity = identity;
        ExpiresAt = expiresAt;
    }

    public string SessionId { get; }
    public ExternalIdentity Identity { get; }
    public DateTimeOffset ExpiresAt { get; }

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

    // Transitions Active→Revoking→Revoked atomically, revokes all lease gates.
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
                lease.Gate.Revoke();
            }

            _state = SessionState.Revoked;
            return activeLeases;
        }
    }
}
