using Microsoft.JSInterop;

namespace MqttProbe.UI.Tests.Components.Charts;

internal sealed class SlowChartRuntime(IJSRuntime inner) : IJSRuntime
{
    internal SlowUpdateModule? Module { get; set; }

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
        IsModuleImport(identifier, args)
            ? new ValueTask<TValue>((TValue)(object)Module!)
            : inner.InvokeAsync<TValue>(identifier, args);

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
        IsModuleImport(identifier, args)
            ? new ValueTask<TValue>((TValue)(object)Module!)
            : inner.InvokeAsync<TValue>(identifier, cancellationToken, args);

    private bool IsModuleImport(string identifier, object?[]? args) =>
        Module is not null
        && identifier == "import"
        && args is [string path]
        && path == ChartRendererInterop.ModulePath;
}

internal sealed class SlowUpdateModule : IJSObjectReference
{
    private readonly Lock _sync = new();
    private readonly List<TaskCompletionSource> _gates = [];
    private readonly List<ChartRendererSpec> _creates = [];
    private readonly List<ChartRendererSpec> _updates = [];
    private readonly List<bool> _resets = [];
    private TaskCompletionSource? _startWaiter;
    private int _inFlight;
    private int _maxInFlight;

    internal bool HoldUpdates { get; set; } = true;

    internal Exception? NextUpdateFailure { get; set; }

    internal ChartRendererSpec[] Creates
    {
        get { lock (_sync) return [.. _creates]; }
    }

    internal ChartRendererSpec[] Updates
    {
        get { lock (_sync) return [.. _updates]; }
    }

    internal bool[] Resets
    {
        get { lock (_sync) return [.. _resets]; }
    }

    internal int CreateCount
    {
        get { lock (_sync) return _creates.Count; }
    }

    internal int UpdateCount
    {
        get { lock (_sync) return _updates.Count; }
    }

    internal int MaxInFlight
    {
        get { lock (_sync) return _maxInFlight; }
    }

    internal async Task WaitForCreateAsync(int count)
    {
        while (true)
        {
            Task waiter;
            lock (_sync)
            {
                if (_creates.Count >= count)
                    return;
                _startWaiter ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                waiter = _startWaiter.Task;
            }

            await waiter.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    internal async Task WaitForUpdateAsync(int count)
    {
        while (true)
        {
            Task waiter;
            lock (_sync)
            {
                if (_updates.Count >= count)
                    return;
                _startWaiter ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                waiter = _startWaiter.Task;
            }

            await waiter.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private void WakeWaitersLocked()
    {
        _startWaiter?.TrySetResult();
        _startWaiter = null;
    }

    internal void ReleaseNextUpdate()
    {
        TaskCompletionSource? gate;
        lock (_sync)
        {
            gate = _gates.Count > 0 ? _gates[0] : null;
            if (gate is not null)
                _gates.RemoveAt(0);
        }

        gate?.TrySetResult();
    }

    internal void ReleaseAll()
    {
        TaskCompletionSource[] gates;
        lock (_sync)
        {
            HoldUpdates = false;
            gates = [.. _gates];
            _gates.Clear();
        }

        foreach (var gate in gates)
            gate.TrySetResult();
    }

    public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
    {
        switch (identifier)
        {
            case "createChart":
                lock (_sync)
                {
                    _creates.Add((ChartRendererSpec)args![3]!);
                    WakeWaitersLocked();
                }

                return (TValue)(object)true;
            case "disposeChart":
                return (TValue)(object)true;
            case "updateChart":
                await RunUpdateAsync((ChartRendererSpec)args![1]!, (bool)args[2]!);
                return (TValue)(object)true;
            default:
                return default!;
        }
    }

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
        InvokeAsync<TValue>(identifier, args);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task RunUpdateAsync(ChartRendererSpec spec, bool resetViewport)
    {
        TaskCompletionSource? gate = null;
        Exception? failure;
        lock (_sync)
        {
            _updates.Add(spec);
            _resets.Add(resetViewport);
            _inFlight++;
            _maxInFlight = Math.Max(_maxInFlight, _inFlight);
            WakeWaitersLocked();
            failure = NextUpdateFailure;
            NextUpdateFailure = null;
            if (HoldUpdates)
            {
                gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _gates.Add(gate);
            }
        }

        try
        {
            if (gate is not null)
                await gate.Task;
            if (failure is not null)
                throw failure;
        }
        finally
        {
            lock (_sync)
                _inFlight--;
        }
    }
}
