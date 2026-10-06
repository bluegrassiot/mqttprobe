using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace MqttProbe.Web.Authentication;

public sealed class DenialStateStore
{
    private readonly TimeProvider _timeProvider;
    private readonly int _capacity;
    private readonly TimeSpan _ttl;
    private readonly ConcurrentDictionary<string, DenialEntry> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _insertionOrder = new();
    private readonly Dictionary<string, LinkedListNode<string>> _insertionNodes = new(StringComparer.Ordinal);
    private readonly Lock _evictionLock = new();

    public DenialStateStore(TimeProvider timeProvider, int capacity = 100, TimeSpan? ttl = null)
    {
        _timeProvider = timeProvider;
        _capacity = capacity;
        _ttl = ttl ?? TimeSpan.FromMinutes(5);
    }

    public string Store(string displayName, string category)
    {
        var handle = GenerateBase64UrlHandle();
        var entry = new DenialEntry(displayName, category, _timeProvider.GetUtcNow().Add(_ttl));

        lock (_evictionLock)
        {
            EvictExpired();

            while (_entries.Count >= _capacity)
            {
                var oldest = _insertionOrder.First!.Value;
                _insertionOrder.RemoveFirst();
                _insertionNodes.Remove(oldest);
                _entries.TryRemove(oldest, out _);
            }

            _entries[handle] = entry;
            _insertionNodes[handle] = _insertionOrder.AddLast(handle);
        }

        return handle;
    }

    public (string DisplayName, string Category)? Retrieve(string handle)
    {
        if (string.IsNullOrEmpty(handle))
        {
            return null;
        }

        lock (_evictionLock)
        {
            if (!_entries.TryRemove(handle, out var entry))
            {
                return null;
            }

            if (_insertionNodes.Remove(handle, out var node))
            {
                _insertionOrder.Remove(node);
            }

            if (entry.ExpiresAt <= _timeProvider.GetUtcNow())
            {
                return null;
            }

            return (entry.DisplayName, entry.Category);
        }
    }

    private void EvictExpired()
    {
        var now = _timeProvider.GetUtcNow();
        var expired = _entries
            .Where(e => e.Value.ExpiresAt <= now)
            .Select(e => e.Key)
            .ToList();

        foreach (var key in expired)
        {
            _entries.TryRemove(key, out _);
            if (_insertionNodes.Remove(key, out var node))
            {
                _insertionOrder.Remove(node);
            }
        }
    }

    internal int InsertionTrackingCount
    {
        get
        {
            lock (_evictionLock)
            {
                return _insertionNodes.Count;
            }
        }
    }

    private static string GenerateBase64UrlHandle()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private sealed record DenialEntry(string DisplayName, string Category, DateTimeOffset ExpiresAt);
}
