using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace MqttProbe.Web.Authentication;

public sealed class AppSessionCircuitHandler : CircuitHandler
{
    private readonly AppSessionCoordinator _coordinator;
    private readonly AuthenticationStateProvider _authStateProvider;
    private readonly CircuitLease _lease;
    private string? _sessionId;
    private string? _issuer;
    private string? _subject;

    public AppSessionCircuitHandler(
        AppSessionCoordinator coordinator,
        AuthenticationStateProvider authStateProvider,
        CircuitLease lease,
        IAuthenticationStateInvalidator authStateInvalidator,
        ILoginNavigationNotifier loginNotifier)
    {
        _coordinator = coordinator;
        _authStateProvider = authStateProvider;
        _lease = lease;
    }

    public override async Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        var authState = await _authStateProvider.GetAuthenticationStateAsync();
        var sessionId = authState.User.FindFirst(AuthClaimTypes.AppSessionId)?.Value;
        var issuer = authState.User.FindFirst(AuthClaimTypes.AppIssuer)?.Value;
        var subject = authState.User.FindFirst(AuthClaimTypes.AppSubject)?.Value;

        if (string.IsNullOrEmpty(sessionId) ||
            string.IsNullOrEmpty(issuer) ||
            string.IsNullOrEmpty(subject))
        {
            // Anonymous or incomplete identity: teardown gate and fail closed
            await _lease.TeardownAsync(cancellationToken: cancellationToken);
            return;
        }

        _sessionId = sessionId;
        _issuer = issuer;
        _subject = subject;

        if (!_lease.TryBind(circuit.Id, null!))
        {
            // Already bound (should not happen)
            await _lease.TeardownAsync(cancellationToken: cancellationToken);
            _sessionId = null;
            _issuer = null;
            _subject = null;
            return;
        }

        var registered = _coordinator.ValidateAndRegister(sessionId, issuer, subject, _lease);

        if (registered is null)
        {
            // Validation failed: unknown/mismatched/expired session
            await _lease.TeardownAsync(cancellationToken: cancellationToken);
            _sessionId = null;
            _issuer = null;
            _subject = null;
        }
    }

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken) => Task.CompletedTask;

    public override async Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        if (_sessionId is null || _issuer is null || _subject is null)
        {
            return;
        }

        var session = _coordinator.GetSession(_sessionId);
        if (session is null || !session.IsValid(_sessionId, _issuer, _subject, DateTimeOffset.UtcNow))
        {
            await _lease.TeardownAsync(cancellationToken: cancellationToken);
            throw new InvalidOperationException("Session has been revoked or expired.");
        }
    }

    public override async Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        // Teardown only this lease; do not revoke sibling circuits. No forced
        // /Login navigation: there is no live UI to navigate when the circuit ends.
        await _lease.TeardownAsync(notifyForceLogin: false, cancellationToken);

        if (_sessionId is not null)
        {
            _coordinator.UnregisterCircuit(_sessionId, circuit.Id);
        }
    }

    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(
        Func<CircuitInboundActivityContext, Task> next)
    {
        return async context =>
        {
            if (!_lease.Gate.IsActive)
            {
                return;
            }

            await next(context);
        };
    }
}
