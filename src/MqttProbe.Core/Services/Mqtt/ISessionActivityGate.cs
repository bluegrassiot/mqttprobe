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
public sealed class RevocableSessionActivityGate : ISessionActivityGate, IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private volatile bool _isActive = true;

    public bool IsActive => _isActive;
    public CancellationToken RevocationToken => _cts.Token;

    public void EnsureActive()
    {
        if (!_isActive)
            throw new InvalidOperationException("Session activity gate has been revoked.");
    }

    public void Revoke()
    {
        if (!_isActive) return;
        _isActive = false;
        _cts.Cancel();
    }

    public void Dispose()
    {
        _cts.Dispose();
    }
}
