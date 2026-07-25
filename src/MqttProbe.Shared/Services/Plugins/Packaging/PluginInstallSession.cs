namespace MqttProbe.Services.Plugins.Packaging;

public sealed class PluginInstallSession
{
    private readonly Lock _gate = new();

    private readonly Dictionary<string, bool> _pending = new(StringComparer.OrdinalIgnoreCase);

    public void Record(string id, bool requiresRestart)
    {
        lock (_gate)
        {
            _pending[id] = requiresRestart || (_pending.TryGetValue(id, out var existing) && existing);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _pending.Clear();
        }
    }

    // Record only ever raises the restart flag, so an id that was fully undone has to be
    // dropped outright: re-recording it as false would leave the earlier true in place and
    // strand a restart notice for a plugin that is no longer installed.
    public void Forget(string id)
    {
        lock (_gate)
        {
            _pending.Remove(id);
        }
    }

    // A reload only ever applies the non-restart entries (schema packages); an entry still
    // waiting on a restart has nothing to apply yet and must stay recorded, or its row loses
    // the pending-operation marker match and falls back to "Installed but not loaded".
    public void ClearNonRestartEntries()
    {
        lock (_gate)
        {
            foreach (var id in _pending.Where(kv => !kv.Value).Select(kv => kv.Key).ToList())
            {
                _pending.Remove(id);
            }
        }
    }

    public bool TryGet(string id, out bool requiresRestart)
    {
        lock (_gate)
        {
            return _pending.TryGetValue(id, out requiresRestart);
        }
    }

    public bool HasPending
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count > 0;
            }
        }
    }

    public bool RequiresRestart
    {
        get
        {
            lock (_gate)
            {
                return _pending.Values.Any(requiresRestart => requiresRestart);
            }
        }
    }

    // Independent of RequiresRestart: an assembly change awaiting restart and a schema
    // change ready to apply can be pending at the same time, and each needs its own banner.
    public bool CanApplyWithoutRestart
    {
        get
        {
            lock (_gate)
            {
                return _pending.Values.Any(requiresRestart => !requiresRestart);
            }
        }
    }
}
