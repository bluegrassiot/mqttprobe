namespace MqttProbe.UI.Utilities;

// The pending slot stays claimed until the dispatched refresh starts, so a busy renderer cannot
// pile up queued renders however many events arrive while it is blocked.
internal sealed class RefreshCoalescer : IDisposable, IAsyncDisposable
{
    private const int Idle = 0;
    private const int Claimed = 1;
    private const int Dispatched = 2;

    private readonly Func<Func<Task>, Task> _invokeAsync;
    private readonly Func<Task> _refresh;
    private readonly Timer _timer;
    private readonly Lock _dispatchLock = new();
    private long _lastRefreshTicks;
    private long _dispatchGeneration;
    private int _state;
    private int _queued;
    private int _executed;
    private volatile bool _disposed;

    internal RefreshCoalescer(Func<Func<Task>, Task> invokeAsync, Func<Task> refresh, int windowMs)
    {
        _invokeAsync = invokeAsync;
        _refresh = refresh;
        WindowMs = windowMs;
        _lastRefreshTicks = Environment.TickCount64;
        _timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    internal int WindowMs { get; set; }

    internal int QueuedCount => Volatile.Read(ref _queued);

    internal int ExecutedCount => Volatile.Read(ref _executed);

    internal bool IsPending => Volatile.Read(ref _state) != Idle;

    internal Func<Func<Task>, Task>? DispatchOverrideForTest { get; set; }

    internal Action? DispatchFailureObservedForTest { get; set; }

    // Callable from any thread; later callers are absorbed until the dispatched refresh releases the slot.
    internal void Request()
    {
        if (_disposed) return;
        if (Interlocked.CompareExchange(ref _state, Claimed, Idle) != Idle) return;

        var elapsed = Environment.TickCount64 - Volatile.Read(ref _lastRefreshTicks);
        var dueMs = elapsed >= WindowMs ? 0L : WindowMs - elapsed;
        try
        {
            _timer.Change(dueMs, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // Disposal won the race with this request; the tick has nothing left to render.
        }
    }

    internal void Tick()
    {
        if (_disposed) return;
        long generation;
        lock (_dispatchLock)
        {
            if (Interlocked.CompareExchange(ref _state, Dispatched, Claimed) != Claimed) return;
            generation = ++_dispatchGeneration;
        }

        Interlocked.Increment(ref _queued);
        try
        {
            var dispatchTask = (DispatchOverrideForTest ?? _invokeAsync)(() => RunRefreshAsync(generation));
            _ = dispatchTask.ContinueWith(
                static (task, state) =>
                {
                    var (coalescer, dispatchGeneration) = ((RefreshCoalescer Coalescer, long Generation))state!;
                    if (!task.IsFaulted && !task.IsCanceled) return;
                    _ = task.Exception;
                    coalescer.ReleaseDispatch(dispatchGeneration);
                    coalescer.DispatchFailureObservedForTest?.Invoke();
                },
                (this, generation),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        catch (ObjectDisposedException)
        {
            // The renderer went away before the refresh could be dispatched.
            ReleaseDispatch(generation);
        }
    }

    private void ReleaseDispatch(long generation)
    {
        lock (_dispatchLock)
        {
            if (_dispatchGeneration != generation) return;
            Interlocked.CompareExchange(ref _state, Idle, Dispatched);
        }
    }

    private async Task RunRefreshAsync(long generation)
    {
        lock (_dispatchLock)
        {
            if (_disposed || _dispatchGeneration != generation || Volatile.Read(ref _state) != Dispatched) return;

            // The window counts from when the refresh actually runs, and the slot is freed only after
            // that stamp is visible, so an event racing the release cannot schedule an immediate render.
            Volatile.Write(ref _lastRefreshTicks, Environment.TickCount64);
            Interlocked.Exchange(ref _state, Idle);
            Interlocked.Increment(ref _executed);
        }

        await _refresh();
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await _timer.DisposeAsync();
    }
}
