namespace MqttProbe.Web.Authentication;

public enum BackChannelLogoutReservation
{
    Reserved,
    Duplicate,
    Full,
}

public sealed class BackChannelLogoutReplayCache
{
    public const int MaxEntries = 4096;

    private sealed class Claim
    {
        public Claim(DateTimeOffset expiresAt, bool completed)
        {
            ExpiresAt = expiresAt;
            Completed = completed;
        }

        public DateTimeOffset ExpiresAt { get; }

        public bool Completed { get; set; }
    }

    private readonly Lock _lock = new();
    private readonly Dictionary<string, Claim> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    public BackChannelLogoutReplayCache(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public int Count
    {
        get { lock (_lock) return _entries.Count; }
    }

    public BackChannelLogoutReservation TryReserve(string issuer, string tokenId, DateTimeOffset expiresAt)
    {
        var key = BuildKey(issuer, tokenId);
        var now = _timeProvider.GetUtcNow();

        lock (_lock)
        {
            RemoveExpired(now);

            if (_entries.ContainsKey(key))
            {
                return BackChannelLogoutReservation.Duplicate;
            }

            // Full rejects instead of evicting: dropping an unexpired claim
            // would reopen a token the validator still accepts.
            if (_entries.Count >= MaxEntries)
            {
                return BackChannelLogoutReservation.Full;
            }

            _entries[key] = new Claim(expiresAt, completed: false);
            return BackChannelLogoutReservation.Reserved;
        }
    }

    public void Complete(string issuer, string tokenId)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(BuildKey(issuer, tokenId), out var claim))
            {
                claim.Completed = true;
            }
        }
    }

    public void Release(string issuer, string tokenId)
    {
        var key = BuildKey(issuer, tokenId);

        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var claim) && !claim.Completed)
            {
                _entries.Remove(key);
            }
        }
    }

    private static string BuildKey(string issuer, string tokenId) => issuer + "\n" + tokenId;

    private void RemoveExpired(DateTimeOffset now)
    {
        var expired = _entries.Where(entry => entry.Value.ExpiresAt <= now).Select(entry => entry.Key).ToList();
        foreach (var key in expired)
        {
            _entries.Remove(key);
        }
    }
}
