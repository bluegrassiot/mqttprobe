using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.Time.Testing;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Core.Services.Security;
using MqttProbe.Web.Authentication;
using SessionState = MqttProbe.Web.Authentication.SessionState;

namespace MqttProbe.UI.Tests.Authentication;

[TestFixture]
public class AppSessionCircuitHandlerTests
{
    private static readonly DateTimeOffset _epoch = new(2026, 8, 29, 8, 0, 0, TimeSpan.Zero);

    private static ClaimsPrincipal CreatePrincipal(
        string sessionId = "sess-1",
        string iss = "https://idp.example.com",
        string sub = "user-123")
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(AuthClaimTypes.AppIssuer, iss),
            new Claim(AuthClaimTypes.AppSubject, sub),
            new Claim(AuthClaimTypes.AppDisplayName, "John Doe"),
            new Claim(AuthClaimTypes.AppSessionId, sessionId),
            new Claim(AuthClaimTypes.AppSessionExpiry, "0"),
            new Claim(ClaimTypes.Role, AppRoles.Admin)
        ], authenticationType: "OIDC");
        return new ClaimsPrincipal(identity);
    }

    private static AppSessionCoordinator CreateCoordinator(
        TimeProvider? timeProvider = null,
        TimeSpan? sessionLifetime = null)
    {
        return new AppSessionCoordinator(
            timeProvider ?? new FakeTimeProvider(_epoch),
            sessionLifetime ?? TimeSpan.FromHours(8));
    }

    private static ExternalIdentity CreateIdentity() =>
        ExternalIdentity.Create("https://idp.example.com", "user-123", "John Doe");

    private static CircuitLease CreateLease(
        RevocableSessionActivityGate? gate = null,
        ICircuitTeardownHandler? teardown = null)
    {
        return new CircuitLease(
            gate ?? new RevocableSessionActivityGate(),
            teardown ?? Substitute.For<ICircuitTeardownHandler>());
    }

    // ── Open validates and registers (via coordinator) ───────────────────────

    [Test]
    public void Open_ValidPrincipal_BindsAndRegistersLease()
    {
        var coordinator = CreateCoordinator();
        var record = coordinator.CreateSession(CreateIdentity());
        var gate = new RevocableSessionActivityGate();
        var teardown = Substitute.For<ICircuitTeardownHandler>();
        var lease = CreateLease(gate, teardown);

        // Bind the lease first (as the handler would do)
        lease.TryBind("c1", record);
        var registered = coordinator.ValidateAndRegister(
            record.SessionId, "https://idp.example.com", "user-123", lease);

        registered.Should().NotBeNull();
        lease.IsBound.Should().BeTrue();
        lease.CircuitId.Should().Be("c1");
        record.CircuitCount.Should().Be(1);
    }

    [Test]
    public void Open_AnonymousPrincipal_DoesNotRegister()
    {
        var coordinator = CreateCoordinator();
        coordinator.CreateSession(CreateIdentity());
        var lease = CreateLease();

        // Anonymous has no valid session ID
        var registered = coordinator.ValidateAndRegister(
            "unknown", "https://idp.example.com", "user-123", lease);

        registered.Should().BeNull();
    }

    [Test]
    public void Open_InvalidSession_DoesNotRegister()
    {
        var coordinator = CreateCoordinator();
        var lease = CreateLease();

        // Session doesn't exist
        var registered = coordinator.ValidateAndRegister(
            "nonexistent", "https://idp.example.com", "user-123", lease);

        registered.Should().BeNull();
    }

    // ── Two distinct circuit IDs ─────────────────────────────────────────────

    [Test]
    public void Open_TwoCircuits_EachGetsOwnLease()
    {
        var coordinator = CreateCoordinator();
        var record = coordinator.CreateSession(CreateIdentity());

        var lease1 = CreateLease();
        var lease2 = CreateLease();

        // Bind and register first circuit
        lease1.TryBind("c1", record);
        coordinator.ValidateAndRegister(record.SessionId, "https://idp.example.com", "user-123", lease1);

        // Bind and register second circuit
        lease2.TryBind("c2", record);
        coordinator.ValidateAndRegister(record.SessionId, "https://idp.example.com", "user-123", lease2);

        lease1.CircuitId.Should().Be("c1");
        lease2.CircuitId.Should().Be("c2");
        lease1.Gate.Should().NotBeSameAs(lease2.Gate);
        record.CircuitCount.Should().Be(2);
    }

    // ── Collision: same circuit ID registered twice ──────────────────────────

    [Test]
    public void Open_SameCircuitId_SecondLeaseCannotRegister()
    {
        var coordinator = CreateCoordinator();
        var record = coordinator.CreateSession(CreateIdentity());

        var lease1 = CreateLease();
        var lease2 = CreateLease();

        // Bind and register first circuit
        lease1.TryBind("c1", record);
        coordinator.ValidateAndRegister(record.SessionId, "https://idp.example.com", "user-123", lease1);

        // Try to bind second lease to same circuit ID
        lease2.TryBind("c1", record);
        var registered = coordinator.ValidateAndRegister(
            record.SessionId, "https://idp.example.com", "user-123", lease2);

        // Second lease returns the existing lease (collision)
        registered.Should().BeSameAs(lease1);
        record.CircuitCount.Should().Be(1);
    }

    // ── Down retains ─────────────────────────────────────────────────────────

    [Test]
    public void ConnectionDown_RetainsCircuit()
    {
        var coordinator = CreateCoordinator();
        var record = coordinator.CreateSession(CreateIdentity());
        var lease = CreateLease();

        lease.TryBind("c1", record);
        coordinator.ValidateAndRegister(record.SessionId, "https://idp.example.com", "user-123", lease);

        // Connection down should not unregister
        record.CircuitCount.Should().Be(1);
    }

    // ── Up revalidates ───────────────────────────────────────────────────────

    [Test]
    public void ConnectionUp_RevokedSession_FailsValidation()
    {
        var coordinator = CreateCoordinator();
        var record = coordinator.CreateSession(CreateIdentity());
        coordinator.RevokeSessionAsync(record.SessionId).Wait();

        // After revocation, session is removed from registry
        var session = coordinator.GetSession(record.SessionId);
        session.Should().BeNull();
    }

    [Test]
    public void ConnectionUp_ExpiredSession_FailsValidation()
    {
        var timeProvider = new FakeTimeProvider(_epoch);
        var coordinator = new AppSessionCoordinator(timeProvider, TimeSpan.FromHours(8));
        var record = coordinator.CreateSession(CreateIdentity());

        // Advance time past expiry
        timeProvider.Advance(TimeSpan.FromHours(9));

        // After expiry, session is removed from registry
        var session = coordinator.GetSession(record.SessionId);
        session.Should().BeNull();
    }

    // ── Close tears down only that lease ─────────────────────────────────────

    [Test]
    public async Task CircuitClosed_TearsDownOnlyThatLease()
    {
        var coordinator = CreateCoordinator();
        var record = coordinator.CreateSession(CreateIdentity());
        var gate1 = new RevocableSessionActivityGate();
        var gate2 = new RevocableSessionActivityGate();
        var teardown1 = Substitute.For<ICircuitTeardownHandler>();
        var teardown2 = Substitute.For<ICircuitTeardownHandler>();
        var lease1 = CreateLease(gate1, teardown1);
        var lease2 = CreateLease(gate2, teardown2);

        lease1.TryBind("c1", record);
        lease2.TryBind("c2", record);
        coordinator.ValidateAndRegister(record.SessionId, "https://idp.example.com", "user-123", lease1);
        coordinator.ValidateAndRegister(record.SessionId, "https://idp.example.com", "user-123", lease2);

        // Close c1
        await lease1.TeardownAsync();

        // c1 gate revoked, c2 gate still active
        gate1.IsActive.Should().BeFalse();
        gate2.IsActive.Should().BeTrue();
        await teardown1.Received(1).TeardownAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await teardown2.DidNotReceive().TeardownAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    // ── Close tears down without force-login navigation ─────────────────────

    [Test]
    public async Task CircuitClosed_SetsAnonymousWithoutNotifyingForceLogin()
    {
        var coordinator = CreateCoordinator();
        var record = coordinator.CreateSession(CreateIdentity());
        var authInvalidator = Substitute.For<IAuthenticationStateInvalidator>();
        var loginNotifier = Substitute.For<ILoginNavigationNotifier>();
        var logger = Substitute.For<Microsoft.Extensions.Logging.ILogger<ScopedCircuitTeardownHandler>>();
        var teardownHandler = new ScopedCircuitTeardownHandler(
            null, null, authInvalidator, loginNotifier, logger);
        var gate = new RevocableSessionActivityGate();
        var lease = CreateLease(gate, teardownHandler);
        lease.TryBind("c1", record);
        coordinator.ValidateAndRegister(record.SessionId, "https://idp.example.com", "user-123", lease);

        var handler = new AppSessionCircuitHandler(
            coordinator,
            Substitute.For<AuthenticationStateProvider>(),
            lease,
            authInvalidator,
            loginNotifier);
        var circuit = (Circuit)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(Circuit));

        await handler.OnCircuitClosedAsync(circuit, CancellationToken.None);

        gate.IsActive.Should().BeFalse();
        authInvalidator.Received(1).SetAnonymous();
        loginNotifier.DidNotReceive().NotifyForceLogin();
    }

    // ── Inbound activity handler ─────────────────────────────────────────────

    [Test]
    public void CreateInboundActivityHandler_GateActive_CallsNext()
    {
        var gate = new RevocableSessionActivityGate();

        var nextCalled = false;
        Func<CircuitInboundActivityContext, Task> next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        // Simulate what CreateInboundActivityHandler does
        Func<CircuitInboundActivityContext, Task> inboundHandler = async context =>
        {
            if (!gate.IsActive)
            {
                return;
            }

            await next(context);
        };

        // Create a real CircuitInboundActivityContext using reflection
        var context = (CircuitInboundActivityContext)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(CircuitInboundActivityContext));
        inboundHandler(context).Wait();

        nextCalled.Should().BeTrue();
    }

    [Test]
    public void CreateInboundActivityHandler_GateRevoked_DoesNotCallNext()
    {
        var gate = new RevocableSessionActivityGate();
        gate.Revoke();

        var nextCalled = false;
        Func<CircuitInboundActivityContext, Task> next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        // Simulate what CreateInboundActivityHandler does
        Func<CircuitInboundActivityContext, Task> inboundHandler = async context =>
        {
            if (!gate.IsActive)
            {
                return;
            }

            await next(context);
        };

        // Create a real CircuitInboundActivityContext using reflection
        var context = (CircuitInboundActivityContext)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(CircuitInboundActivityContext));
        inboundHandler(context).Wait();

        nextCalled.Should().BeFalse();
    }

    // ── Lease binding ────────────────────────────────────────────────────────

    [Test]
    public void TryBind_FirstBind_ReturnsTrue()
    {
        var lease = CreateLease();
        var coordinator = CreateCoordinator();
        var record = coordinator.CreateSession(CreateIdentity());

        var result = lease.TryBind("c1", record);

        result.Should().BeTrue();
        lease.IsBound.Should().BeTrue();
        lease.CircuitId.Should().Be("c1");
    }

    [Test]
    public void TryBind_SecondBind_ReturnsFalse()
    {
        var lease = CreateLease();
        var coordinator = CreateCoordinator();
        var record = coordinator.CreateSession(CreateIdentity());

        lease.TryBind("c1", record);
        var result = lease.TryBind("c2", record);

        result.Should().BeFalse();
        lease.CircuitId.Should().Be("c1");
    }

    [Test]
    public void IsBound_InitiallyFalse()
    {
        var lease = CreateLease();

        lease.IsBound.Should().BeFalse();
        lease.CircuitId.Should().BeEmpty();
    }

    // ── Real handler lifecycle: open ─────────────────────────────────────────

    private static AuthenticationStateProvider CreateAuthProvider(ClaimsPrincipal user)
    {
        var provider = Substitute.For<AuthenticationStateProvider>();
        provider.GetAuthenticationStateAsync().Returns(new AuthenticationState(user));
        return provider;
    }

    private static AppSessionCircuitHandler CreateHandler(
        AppSessionCoordinator coordinator,
        AuthenticationStateProvider authState,
        CircuitLease lease)
    {
        return new AppSessionCircuitHandler(
            coordinator,
            authState,
            lease,
            Substitute.For<IAuthenticationStateInvalidator>(),
            Substitute.For<ILoginNavigationNotifier>());
    }

    // Circuit's only constructor is internal and takes a CircuitHost, and
    // Circuit.Id is delegated to that host. Both types are internal to the
    // framework assembly, so build the smallest object graph that still yields
    // a stable Id for the lease to bind to.
    private static Circuit CreateCircuit(string id)
    {
        var assembly = typeof(Circuit).Assembly;
        var hostType = assembly.GetType("Microsoft.AspNetCore.Components.Server.Circuits.CircuitHost")!;
        var circuitIdType = assembly.GetType("Microsoft.AspNetCore.Components.Server.Circuits.CircuitId")!;

        var circuitId = circuitIdType
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public)[0]
            .Invoke(["secret", id]);

        var host = RuntimeHelpers.GetUninitializedObject(hostType);
        hostType
            .GetField("<CircuitId>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(host, circuitId);

        var circuitCtor = typeof(Circuit).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)[0];
        return (Circuit)circuitCtor.Invoke([host]);
    }

    private static CircuitInboundActivityContext CreateInboundContext(Circuit circuit)
    {
        var ctor = typeof(CircuitInboundActivityContext)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)[0];
        return (CircuitInboundActivityContext)ctor.Invoke([null, circuit]);
    }

    [Test]
    public async Task CircuitOpened_ValidPrincipal_BindsAndRegistersLease()
    {
        var coordinator = CreateCoordinator();
        var record = coordinator.CreateSession(CreateIdentity());
        var teardown = Substitute.For<ICircuitTeardownHandler>();
        var lease = CreateLease(teardown: teardown);
        var handler = CreateHandler(
            coordinator, CreateAuthProvider(CreatePrincipal(sessionId: record.SessionId)), lease);
        var circuit = CreateCircuit("c1");

        await handler.OnCircuitOpenedAsync(circuit, CancellationToken.None);

        lease.IsBound.Should().BeTrue();
        lease.CircuitId.Should().Be("c1");
        record.CircuitCount.Should().Be(1);
        record.GetLease("c1").Should().BeSameAs(lease);
        await teardown.DidNotReceive().TeardownAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CircuitOpened_AnonymousPrincipal_TearsDownWithoutBinding()
    {
        var coordinator = CreateCoordinator();
        var record = coordinator.CreateSession(CreateIdentity());
        var teardown = Substitute.For<ICircuitTeardownHandler>();
        var lease = CreateLease(teardown: teardown);
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity(authenticationType: "OIDC"));
        var handler = CreateHandler(coordinator, CreateAuthProvider(anonymous), lease);
        var circuit = CreateCircuit("c1");

        await handler.OnCircuitOpenedAsync(circuit, CancellationToken.None);

        lease.IsBound.Should().BeFalse();
        record.CircuitCount.Should().Be(0);
        await teardown.Received(1).TeardownAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CircuitOpened_IncompleteIdentity_TearsDownWithoutBinding()
    {
        var coordinator = CreateCoordinator();
        var record = coordinator.CreateSession(CreateIdentity());
        var teardown = Substitute.For<ICircuitTeardownHandler>();
        var lease = CreateLease(teardown: teardown);

        // Session and issuer are present but the subject claim is missing.
        var partial = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(AuthClaimTypes.AppIssuer, "https://idp.example.com"),
            new Claim(AuthClaimTypes.AppSessionId, record.SessionId)
        ], authenticationType: "OIDC"));
        var handler = CreateHandler(coordinator, CreateAuthProvider(partial), lease);
        var circuit = CreateCircuit("c1");

        await handler.OnCircuitOpenedAsync(circuit, CancellationToken.None);

        lease.IsBound.Should().BeFalse();
        record.CircuitCount.Should().Be(0);
        await teardown.Received(1).TeardownAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CircuitOpened_AlreadyBoundLease_TearsDownAndClearsState()
    {
        var coordinator = CreateCoordinator();
        var record = coordinator.CreateSession(CreateIdentity());
        var teardown = Substitute.For<ICircuitTeardownHandler>();
        var lease = CreateLease(teardown: teardown);
        lease.TryBind("pre-bound", record).Should().BeTrue();

        var handler = CreateHandler(
            coordinator, CreateAuthProvider(CreatePrincipal(sessionId: record.SessionId)), lease);
        var circuit = CreateCircuit("c1");

        await handler.OnCircuitOpenedAsync(circuit, CancellationToken.None);

        record.CircuitCount.Should().Be(0);
        await teardown.Received(1).TeardownAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());

        // Cleared state makes the reconnect path bail out instead of validating.
        await handler.OnConnectionUpAsync(circuit, CancellationToken.None);
    }

    [Test]
    public async Task CircuitOpened_UnknownSession_TearsDownAndClearsState()
    {
        var coordinator = CreateCoordinator();
        var teardown = Substitute.For<ICircuitTeardownHandler>();
        var lease = CreateLease(teardown: teardown);
        var handler = CreateHandler(
            coordinator, CreateAuthProvider(CreatePrincipal()), lease);
        var circuit = CreateCircuit("c1");

        await handler.OnCircuitOpenedAsync(circuit, CancellationToken.None);

        lease.IsBound.Should().BeTrue();
        await teardown.Received(1).TeardownAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());

        // Cleared state makes the reconnect path bail out instead of validating.
        await handler.OnConnectionUpAsync(circuit, CancellationToken.None);
    }

    // ── Real handler lifecycle: down / up ────────────────────────────────────

    [Test]
    public async Task ConnectionDown_LeavesLeaseUntouched()
    {
        var coordinator = CreateCoordinator();
        var record = coordinator.CreateSession(CreateIdentity());
        var lease = CreateLease();
        var handler = CreateHandler(
            coordinator, CreateAuthProvider(CreatePrincipal(sessionId: record.SessionId)), lease);
        var circuit = CreateCircuit("c1");
        await handler.OnCircuitOpenedAsync(circuit, CancellationToken.None);

        await handler.OnConnectionDownAsync(circuit, CancellationToken.None);

        lease.IsBound.Should().BeTrue();
        record.CircuitCount.Should().Be(1);
    }

    [Test]
    public async Task ConnectionUp_BeforeOpen_ReturnsWithoutValidating()
    {
        var coordinator = CreateCoordinator();
        var teardown = Substitute.For<ICircuitTeardownHandler>();
        var lease = CreateLease(teardown: teardown);
        var handler = CreateHandler(coordinator, CreateAuthProvider(CreatePrincipal()), lease);
        var circuit = CreateCircuit("c1");

        await handler.OnConnectionUpAsync(circuit, CancellationToken.None);

        await teardown.DidNotReceive().TeardownAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ConnectionUp_ValidSession_DoesNotThrow()
    {
        var coordinator = CreateCoordinator(
            new FakeTimeProvider(DateTimeOffset.UtcNow), TimeSpan.FromHours(8));
        var record = coordinator.CreateSession(CreateIdentity());
        var teardown = Substitute.For<ICircuitTeardownHandler>();
        var lease = CreateLease(teardown: teardown);
        var handler = CreateHandler(
            coordinator, CreateAuthProvider(CreatePrincipal(sessionId: record.SessionId)), lease);
        var circuit = CreateCircuit("c1");
        await handler.OnCircuitOpenedAsync(circuit, CancellationToken.None);

        await handler.OnConnectionUpAsync(circuit, CancellationToken.None);

        await teardown.DidNotReceive().TeardownAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ConnectionUp_RevokedSession_TearsDownAndThrows()
    {
        var coordinator = CreateCoordinator();
        var record = coordinator.CreateSession(CreateIdentity());
        var teardown = Substitute.For<ICircuitTeardownHandler>();
        var lease = CreateLease(teardown: teardown);
        var handler = CreateHandler(
            coordinator, CreateAuthProvider(CreatePrincipal(sessionId: record.SessionId)), lease);
        var circuit = CreateCircuit("c1");
        await handler.OnCircuitOpenedAsync(circuit, CancellationToken.None);
        await coordinator.RevokeSessionAsync(record.SessionId);

        Func<Task> act = () => handler.OnConnectionUpAsync(circuit, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        record.State.Should().Be(SessionState.Revoked);
    }

    [Test]
    public async Task ConnectionUp_ExpiredSession_TearsDownAndThrows()
    {
        // Session expires 30 minutes before the real clock while the coordinator's
        // fake clock still sees 30 minutes of lifetime left, so the record survives
        // in the registry but fails the handler's wall-clock validity check.
        var fakeNow = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);
        var coordinator = CreateCoordinator(
            new FakeTimeProvider(fakeNow), TimeSpan.FromMinutes(30));
        var record = coordinator.CreateSession(CreateIdentity());
        var teardown = Substitute.For<ICircuitTeardownHandler>();
        var lease = CreateLease(teardown: teardown);
        var handler = CreateHandler(
            coordinator, CreateAuthProvider(CreatePrincipal(sessionId: record.SessionId)), lease);
        var circuit = CreateCircuit("c1");
        await handler.OnCircuitOpenedAsync(circuit, CancellationToken.None);

        Func<Task> act = () => handler.OnConnectionUpAsync(circuit, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        coordinator.GetSession(record.SessionId).Should().NotBeNull();
    }

    // ── Real handler lifecycle: close ────────────────────────────────────────

    [Test]
    public async Task CircuitClosed_AfterOpen_UnregistersCircuitWithoutForceLogin()
    {
        var coordinator = CreateCoordinator();
        var record = coordinator.CreateSession(CreateIdentity());
        var authInvalidator = Substitute.For<IAuthenticationStateInvalidator>();
        var loginNotifier = Substitute.For<ILoginNavigationNotifier>();
        var teardown = Substitute.For<ICircuitTeardownHandler>();
        var gate = new RevocableSessionActivityGate();
        var lease = new CircuitLease(gate, teardown);
        var handler = new AppSessionCircuitHandler(
            coordinator,
            CreateAuthProvider(CreatePrincipal(sessionId: record.SessionId)),
            lease,
            authInvalidator,
            loginNotifier);
        var circuit = CreateCircuit("c1");
        await handler.OnCircuitOpenedAsync(circuit, CancellationToken.None);
        record.CircuitCount.Should().Be(1);

        await handler.OnCircuitClosedAsync(circuit, CancellationToken.None);

        record.CircuitCount.Should().Be(0);
        record.GetLease("c1").Should().BeNull();
        gate.IsActive.Should().BeFalse();
        await teardown.Received(1).TeardownAsync(
            Arg.Is<bool>(notify => !notify), Arg.Any<CancellationToken>());
        loginNotifier.DidNotReceive().NotifyForceLogin();
    }

    // ── Real handler lifecycle: inbound activity ─────────────────────────────

    [Test]
    public async Task InboundActivity_GateActive_CallsNext()
    {
        var handler = CreateHandler(
            CreateCoordinator(),
            CreateAuthProvider(CreatePrincipal()),
            CreateLease(new RevocableSessionActivityGate()));

        var nextCalled = false;
        Func<CircuitInboundActivityContext, Task> next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var inbound = handler.CreateInboundActivityHandler(next);
        await inbound(CreateInboundContext(CreateCircuit("c1")));

        nextCalled.Should().BeTrue();
    }

    [Test]
    public async Task InboundActivity_GateRevoked_SkipsNext()
    {
        var gate = new RevocableSessionActivityGate();
        gate.Revoke();
        var handler = CreateHandler(
            CreateCoordinator(),
            CreateAuthProvider(CreatePrincipal()),
            CreateLease(gate));

        var nextCalled = false;
        Func<CircuitInboundActivityContext, Task> next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var inbound = handler.CreateInboundActivityHandler(next);
        await inbound(CreateInboundContext(CreateCircuit("c1")));

        nextCalled.Should().BeFalse();
    }
}
