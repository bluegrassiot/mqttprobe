using MqttProbe.Core.Services.Mqtt;

namespace MqttProbe.Web.Authentication;

public sealed class CircuitLease
{
    private readonly ICircuitTeardownHandler _teardownHandler;
    private int _tornDown;

    public CircuitLease(
        RevocableSessionActivityGate gate,
        ICircuitTeardownHandler teardownHandler)
    {
        Gate = gate;
        _teardownHandler = teardownHandler;
    }

    public string CircuitId { get; private set; } = "";
    public AppSessionRecord? Session { get; private set; }
    public RevocableSessionActivityGate Gate { get; }
    public bool IsBound => !string.IsNullOrEmpty(CircuitId);

    internal bool TryBind(string circuitId, AppSessionRecord session)
    {
        if (IsBound)
        {
            return false;
        }

        CircuitId = circuitId;
        Session = session;
        return true;
    }

    // Idempotent teardown: only the first caller reaches the handler.
    public Task TeardownAsync(bool notifyForceLogin = true, CancellationToken cancellationToken = default)
        => RunTeardownAsync(() => _teardownHandler.TeardownAsync(notifyForceLogin, cancellationToken));

    // Same idempotent teardown, but the navigation decision is re-read when the
    // handler reaches its notification step, so an explicit logout landing
    // mid-teardown still suppresses the forced /Login navigation.
    public Task TeardownAsync(Func<bool> shouldNotifyForceLogin, CancellationToken cancellationToken = default)
        => RunTeardownAsync(() => _teardownHandler.TeardownAsync(shouldNotifyForceLogin, cancellationToken));

    private async Task RunTeardownAsync(Func<Task> invokeTeardownHandler)
    {
        if (Interlocked.Exchange(ref _tornDown, 1) != 0)
        {
            return;
        }

        // Revoke() closes the gate without waiting on its callbacks, so cleanup
        // starts even while a revocation callback is still running.
        Gate.Revoke();
        await invokeTeardownHandler().ConfigureAwait(false);
    }
}

public interface ICircuitTeardownHandler
{
    public Task TeardownAsync(bool notifyForceLogin = true, CancellationToken cancellationToken = default);

    public Task TeardownAsync(Func<bool> shouldNotifyForceLogin, CancellationToken cancellationToken = default);
}
