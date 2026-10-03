using Microsoft.Extensions.Logging;

namespace MqttProbe.Core.Services.Mqtt;

public interface ISessionActivityGate
{
    public bool IsActive { get; }
    public CancellationToken RevocationToken { get; }
    public void EnsureActive();
    public void Revoke();
}

// Default gate for Desktop, MAUI, and single-session Core hosts.
public sealed class AlwaysActiveSessionActivityGate : ISessionActivityGate
{
    public bool IsActive => true;
    public CancellationToken RevocationToken => CancellationToken.None;
    public void EnsureActive() { }
    public void Revoke() { }
}

// Gate that can be permanently revoked. After Revoke(), IsActive returns false,
// EnsureActive throws, and RevocationToken is cancelled. Thread-safe. Idempotent.
// Dispose implies Revoke and releases the source only after every callback has
// run: the gate can never look closed while its cancellation is still missing.
public sealed class RevocableSessionActivityGate : ISessionActivityGate, IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _lock = new();
    private readonly ILogger<RevocableSessionActivityGate>? _logger;
    private Task? _revocation;
    private bool _disposeContinuationScheduled;
    private bool _ctsDisposed;
    private int _active = 1;

    public RevocableSessionActivityGate(ILogger<RevocableSessionActivityGate>? logger = null)
        => _logger = logger;

    public bool IsActive => Volatile.Read(ref _active) == 1;

    public CancellationToken RevocationToken => _cts.Token;

    // Completes once every revocation callback has run, including one that
    // blocks. Callback failures are logged rather than rethrown here, so
    // callers can await this without observing a fault.
    public Task RevocationCompletion
    {
        get { lock (_lock) return _revocation ?? Task.CompletedTask; }
    }

    public void EnsureActive()
    {
        if (!IsActive)
            throw new InvalidOperationException("Session activity gate has been revoked.");
    }

    // Closes the gate and publishes the revocation in one critical section, so
    // an observer that reads IsActive false can never see RevocationCompletion
    // still standing in for an unstarted revocation. Cancellation begins only
    // after the gate is closed, so a callback that blocks can never keep the
    // gate admitting activity, and this never waits for a callback to finish.
    public void Revoke()
    {
        lock (_lock)
        {
            RevokeCore();
        }
    }

    // Caller holds _lock. The published task is at once the closed marker and
    // the completion handle, so the transition happens exactly once.
    private void RevokeCore()
    {
        if (_revocation is not null)
        {
            return;
        }

        Volatile.Write(ref _active, 0);
        _revocation = CancelAsync();
    }

    private async Task CancelAsync()
    {
        try
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Session activity gate revocation callback failed");
        }
    }

    // Dispose implies Revoke, so a later Revoke can never reach a disposed
    // source and leave the gate closed without ever cancelling its token. The
    // source is released only after every callback has run; one that blocks
    // forever keeps it. Idempotent: repeated calls dispose once and never
    // stack continuations.
    public void Dispose()
    {
        lock (_lock)
        {
            RevokeCore();

            if (_revocation is { IsCompleted: true })
            {
                DisposeCtsCore();
                return;
            }

            if (_disposeContinuationScheduled)
            {
                return;
            }

            _disposeContinuationScheduled = true;

            // Callbacks are still running against this source: they own the
            // dispose once they are done instead of racing it now.
            _ = _revocation!.ContinueWith(
                static (revocation, state) =>
                {
                    // A late fault is observed here rather than dropped with
                    // this discarded continuation.
                    _ = revocation.Exception;
                    ((RevocableSessionActivityGate)state!).DisposeCts();
                },
                this,
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }
    }

    private void DisposeCts()
    {
        lock (_lock)
        {
            DisposeCtsCore();
        }
    }

    private void DisposeCtsCore()
    {
        if (_ctsDisposed)
        {
            return;
        }

        _ctsDisposed = true;
        _cts.Dispose();
    }
}
