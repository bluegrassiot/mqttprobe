using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Time.Testing;
using MqttProbe.Core.Services.Emulation;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Core.Services.Security;
using MqttProbe.Web.Authentication;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SessionState = MqttProbe.Web.Authentication.SessionState;

namespace MqttProbe.UI.Tests.Authentication;

[TestFixture]
public class AppSessionCoordinatorTests
{
    private static readonly DateTimeOffset _epoch = new(2026, 8, 29, 8, 0, 0, TimeSpan.Zero);

    private static AppSessionCoordinator CreateCoordinator(
        TimeProvider? timeProvider = null,
        TimeSpan? sessionLifetime = null)
    {
        return new AppSessionCoordinator(
            timeProvider ?? new FakeTimeProvider(_epoch),
            sessionLifetime ?? TimeSpan.FromHours(8));
    }

    private static ExternalIdentity CreateIdentity(
        string iss = "https://idp.example.com",
        string sub = "user-123",
        string displayName = "John Doe")
    {
        return ExternalIdentity.Create(iss, sub, displayName);
    }

    private static CircuitLease CreateLease(
        string circuitId = "c1",
        RevocableSessionActivityGate? gate = null,
        ICircuitTeardownHandler? teardown = null)
    {
        var lease = new CircuitLease(
            gate ?? new RevocableSessionActivityGate(),
            teardown ?? Substitute.For<ICircuitTeardownHandler>());
        return lease;
    }

    private static CircuitLease CreateBoundLease(
        AppSessionRecord record,
        string circuitId = "c1",
        RevocableSessionActivityGate? gate = null,
        ICircuitTeardownHandler? teardown = null)
    {
        var lease = CreateLease(circuitId, gate, teardown);
        lease.TryBind(circuitId, record);
        return lease;
    }

    // ── ExternalIdentity validation ──────────────────────────────────────────

    [Test]
    public void ExternalIdentity_BlankIssuer_Throws()
    {
        var act = () => ExternalIdentity.Create("", "user-123", "John Doe");

        act.Should().Throw<ArgumentException>().WithParameterName("issuer");
    }

    [Test]
    public void ExternalIdentity_BlankSubject_Throws()
    {
        var act = () => ExternalIdentity.Create("https://idp.example.com", "", "John Doe");

        act.Should().Throw<ArgumentException>().WithParameterName("subject");
    }

    [Test]
    public void ExternalIdentity_Valid_CreatesInstance()
    {
        var identity = CreateIdentity();

        identity.Issuer.Should().Be("https://idp.example.com");
        identity.Subject.Should().Be("user-123");
        identity.DisplayName.Should().Be("John Doe");
    }

    // ── Session creation ─────────────────────────────────────────────────────

    [Test]
    public void CreateSession_ReturnsNonNullRecord()
    {
        var coordinator = CreateCoordinator();

        var record = coordinator.CreateSession(CreateIdentity());

        record.Should().NotBeNull();
    }

    [Test]
    public void CreateSession_GeneratesRandomSessionId()
    {
        var coordinator = CreateCoordinator();

        var r1 = coordinator.CreateSession(CreateIdentity());
        var r2 = coordinator.CreateSession(CreateIdentity());

        r1.SessionId.Should().NotBe(r2.SessionId);
    }

    [Test]
    public void CreateSession_StoresIdentity()
    {
        var coordinator = CreateCoordinator();
        var identity = CreateIdentity(iss: "https://keycloak.local", sub: "alice-42", displayName: "Alice");

        var record = coordinator.CreateSession(identity);

        record.Issuer.Should().Be("https://keycloak.local");
        record.Subject.Should().Be("alice-42");
        record.DisplayName.Should().Be("Alice");
    }

    [Test]
    public void CreateSession_SetsAbsoluteExpiry()
    {
        var timeProvider = new FakeTimeProvider(_epoch);
        var coordinator = CreateCoordinator(timeProvider: timeProvider, sessionLifetime: TimeSpan.FromHours(8));

        var record = coordinator.CreateSession(CreateIdentity());

        record.ExpiresAt.Should().Be(_epoch.Add(TimeSpan.FromHours(8)));
    }

    [Test]
    public void CreateSession_InitialStateIsActive()
    {
        var coordinator = CreateCoordinator();

        var record = coordinator.CreateSession(CreateIdentity());

        record.State.Should().Be(SessionState.Active);
    }

    // ── Atomic validation ────────────────────────────────────────────────────

    [Test]
    public void IsValid_CorrectParameters_ReturnsTrue()
    {
        var coordinator = CreateCoordinator();
        var record = coordinator.CreateSession(CreateIdentity());

        record.IsValid(record.SessionId, "https://idp.example.com", "user-123", _epoch).Should().BeTrue();
    }

    [Test]
    public void IsValid_WrongSessionId_ReturnsFalse()
    {
        var coordinator = CreateCoordinator();
        var record = coordinator.CreateSession(CreateIdentity());

        record.IsValid("wrong-id", "https://idp.example.com", "user-123", _epoch).Should().BeFalse();
    }

    [Test]
    public void IsValid_WrongIssuer_ReturnsFalse()
    {
        var coordinator = CreateCoordinator();
        var record = coordinator.CreateSession(CreateIdentity());

        record.IsValid(record.SessionId, "https://evil.com", "user-123", _epoch).Should().BeFalse();
    }

    [Test]
    public void IsValid_WrongSubject_ReturnsFalse()
    {
        var coordinator = CreateCoordinator();
        var record = coordinator.CreateSession(CreateIdentity());

        record.IsValid(record.SessionId, "https://idp.example.com", "wrong-user", _epoch).Should().BeFalse();
    }

    [Test]
    public void IsValid_Expired_ReturnsFalse()
    {
        var coordinator = CreateCoordinator();
        var record = coordinator.CreateSession(CreateIdentity());

        record.IsValid(record.SessionId, "https://idp.example.com", "user-123", _epoch.AddHours(9)).Should().BeFalse();
    }

    [Test]
    public void IsValid_Revoked_ReturnsFalse()
    {
        var coordinator = CreateCoordinator();
        var record = coordinator.CreateSession(CreateIdentity());
        coordinator.RevokeSessionAsync(record.SessionId).Wait();

        record.IsValid(record.SessionId, "https://idp.example.com", "user-123", _epoch).Should().BeFalse();
    }

    // ── Expiry-1 tick active / expiry inactive ───────────────────────────────

    [Test]
    public void Session_ExpiryMinusOneTick_Active()
    {
        var timeProvider = new FakeTimeProvider(_epoch);
        var coordinator = CreateCoordinator(timeProvider: timeProvider, sessionLifetime: TimeSpan.FromHours(8));
        var record = coordinator.CreateSession(CreateIdentity());

        record.IsValid(record.SessionId, "https://idp.example.com", "user-123",
            _epoch.Add(TimeSpan.FromHours(8)).AddTicks(-1)).Should().BeTrue();
    }

    [Test]
    public void Session_AtExpiry_Inactive()
    {
        var timeProvider = new FakeTimeProvider(_epoch);
        var coordinator = CreateCoordinator(timeProvider: timeProvider, sessionLifetime: TimeSpan.FromHours(8));
        var record = coordinator.CreateSession(CreateIdentity());

        record.IsValid(record.SessionId, "https://idp.example.com", "user-123",
            _epoch.Add(TimeSpan.FromHours(8))).Should().BeFalse();
    }

    // ── One-shot exact deadline ──────────────────────────────────────────────

    [Test]
    public async Task ExpiredSession_IsAutomaticallyRevoked()
    {
        var timeProvider = new FakeTimeProvider(_epoch);
        var coordinator = CreateCoordinator(timeProvider: timeProvider, sessionLifetime: TimeSpan.FromHours(8));
        var record = coordinator.CreateSession(CreateIdentity());

        timeProvider.Advance(TimeSpan.FromHours(8));

        // Timer fires synchronously in FakeTimeProvider
        record.State.Should().Be(SessionState.Revoked);
    }

    [Test]
    public void Session_BeforeExpiry_RemainsActive()
    {
        var timeProvider = new FakeTimeProvider(_epoch);
        var coordinator = CreateCoordinator(timeProvider: timeProvider, sessionLifetime: TimeSpan.FromHours(8));
        var record = coordinator.CreateSession(CreateIdentity());

        timeProvider.Advance(TimeSpan.FromHours(7));

        record.State.Should().Be(SessionState.Active);
    }

    [Test]
    public async Task Session_CreatedOffCycle_ExpiresAtCorrectTime()
    {
        var timeProvider = new FakeTimeProvider(_epoch);
        var coordinator = CreateCoordinator(timeProvider: timeProvider, sessionLifetime: TimeSpan.FromHours(8));

        // Advance time before creating session
        timeProvider.Advance(TimeSpan.FromHours(2));
        var record = coordinator.CreateSession(CreateIdentity());

        // Should expire at _epoch + 2h + 8h = _epoch + 10h
        record.ExpiresAt.Should().Be(_epoch.Add(TimeSpan.FromHours(10)));

        // At _epoch + 9h59m, still active
        timeProvider.Advance(TimeSpan.FromHours(7) + TimeSpan.FromMinutes(59));
        record.State.Should().Be(SessionState.Active);

        // At _epoch + 10h, revoked
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        record.State.Should().Be(SessionState.Revoked);
    }

    // ── ValidateAndRegister ──────────────────────────────────────────────────

    [Test]
    public void ValidateAndRegister_ValidSession_ReturnsLease()
    {
        var coordinator = CreateCoordinator();
        var record = coordinator.CreateSession(CreateIdentity());
        var lease = CreateBoundLease(record);

        var result = coordinator.ValidateAndRegister(
            record.SessionId, "https://idp.example.com", "user-123", lease);

        result.Should().BeSameAs(lease);
    }

    [Test]
    public void ValidateAndRegister_UnknownSession_ReturnsNull()
    {
        var coordinator = CreateCoordinator();
        var record = coordinator.CreateSession(CreateIdentity());
        var lease = CreateBoundLease(record);

        var result = coordinator.ValidateAndRegister(
            "unknown", "https://idp.example.com", "user-123", lease);

        result.Should().BeNull();
    }

    [Test]
    public void ValidateAndRegister_WrongIssuer_ReturnsNull()
    {
        var coordinator = CreateCoordinator();
        var record = coordinator.CreateSession(CreateIdentity());
        var lease = CreateBoundLease(record);

        var result = coordinator.ValidateAndRegister(
            record.SessionId, "https://evil.com", "user-123", lease);

        result.Should().BeNull();
    }

    [Test]
    public void ValidateAndRegister_ExpiredSession_ReturnsNull()
    {
        var timeProvider = new FakeTimeProvider(_epoch);
        var coordinator = CreateCoordinator(timeProvider: timeProvider, sessionLifetime: TimeSpan.FromHours(8));
        var record = coordinator.CreateSession(CreateIdentity());
        var lease = CreateBoundLease(record);

        timeProvider.Advance(TimeSpan.FromHours(8));

        var result = coordinator.ValidateAndRegister(
            record.SessionId, "https://idp.example.com", "user-123", lease);

        result.Should().BeNull();
    }

    [Test]
    public async Task ValidateAndRegister_RevokedSession_ReturnsNull()
    {
        var coordinator = CreateCoordinator();
        var record = coordinator.CreateSession(CreateIdentity());
        await coordinator.RevokeSessionAsync(record.SessionId);
        var lease = CreateBoundLease(record);

        var result = coordinator.ValidateAndRegister(
            record.SessionId, "https://idp.example.com", "user-123", lease);

        result.Should().BeNull();
    }

    // ── RevokeSessionAsync ───────────────────────────────────────────────────

    [Test]
    public async Task RevokeSessionAsync_TransitionsToRevoked()
    {
        var coordinator = CreateCoordinator();
        var record = coordinator.CreateSession(CreateIdentity());

        await coordinator.RevokeSessionAsync(record.SessionId);

        record.State.Should().Be(SessionState.Revoked);
    }

    [Test]
    public async Task RevokeSessionAsync_RevokesAllLeaseGates()
    {
        var coordinator = CreateCoordinator();
        var record = coordinator.CreateSession(CreateIdentity());
        var gate1 = new RevocableSessionActivityGate();
        var gate2 = new RevocableSessionActivityGate();
        var teardown = Substitute.For<ICircuitTeardownHandler>();
        coordinator.ValidateAndRegister(record.SessionId, "https://idp.example.com", "user-123",
            CreateBoundLease(record, "c1", gate1, teardown));
        coordinator.ValidateAndRegister(record.SessionId, "https://idp.example.com", "user-123",
            CreateBoundLease(record, "c2", gate2, teardown));

        await coordinator.RevokeSessionAsync(record.SessionId);

        gate1.IsActive.Should().BeFalse();
        gate2.IsActive.Should().BeFalse();
    }

    [Test]
    public async Task RevokeSessionAsync_Idempotent()
    {
        var coordinator = CreateCoordinator();
        var record = coordinator.CreateSession(CreateIdentity());

        await coordinator.RevokeSessionAsync(record.SessionId);
        var act = () => coordinator.RevokeSessionAsync(record.SessionId);

        await act.Should().NotThrowAsync();
    }

    [Test]
    public async Task RevokeSessionAsync_UnknownSession_DoesNotThrow()
    {
        var coordinator = CreateCoordinator();

        var act = () => coordinator.RevokeSessionAsync("nonexistent");

        await act.Should().NotThrowAsync();
    }

    [Test]
    public async Task RevokeSessionAsync_FiresEvent()
    {
        var coordinator = CreateCoordinator();
        var record = coordinator.CreateSession(CreateIdentity());
        AppSessionRecord? eventRecord = null;
        coordinator.SessionRevoked += r => eventRecord = r;

        await coordinator.RevokeSessionAsync(record.SessionId);

        eventRecord.Should().BeSameAs(record);
    }

    // ── Async revocation with teardown and removal ───────────────────────────

    [Test]
    public async Task RevokeSessionAsync_TearsDownAllLeases()
    {
        var coordinator = CreateCoordinator();
        var record = coordinator.CreateSession(CreateIdentity());
        var teardown1 = Substitute.For<ICircuitTeardownHandler>();
        var teardown2 = Substitute.For<ICircuitTeardownHandler>();
        coordinator.ValidateAndRegister(record.SessionId, "https://idp.example.com", "user-123",
            CreateBoundLease(record, "c1", new RevocableSessionActivityGate(), teardown1));
        coordinator.ValidateAndRegister(record.SessionId, "https://idp.example.com", "user-123",
            CreateBoundLease(record, "c2", new RevocableSessionActivityGate(), teardown2));

        await coordinator.RevokeSessionAsync(record.SessionId);

        await teardown1.Received(1).TeardownAsync(Arg.Any<Func<bool>>(), Arg.Any<CancellationToken>());
        await teardown2.Received(1).TeardownAsync(Arg.Any<Func<bool>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RevokeSessionAsync_RemovesSessionFromRegistry()
    {
        var coordinator = CreateCoordinator();
        var record = coordinator.CreateSession(CreateIdentity());

        await coordinator.RevokeSessionAsync(record.SessionId);

        coordinator.GetSession(record.SessionId).Should().BeNull();
    }

    // ── Zero-circuit sessions retained until expiry ──────────────────────────

    [Test]
    public void Session_ZeroCircuits_RetainedUntilExpiry()
    {
        var timeProvider = new FakeTimeProvider(_epoch);
        var coordinator = CreateCoordinator(timeProvider: timeProvider, sessionLifetime: TimeSpan.FromHours(8));
        var record = coordinator.CreateSession(CreateIdentity());

        timeProvider.Advance(TimeSpan.FromHours(4));

        record.State.Should().Be(SessionState.Active);
        coordinator.GetSession(record.SessionId).Should().NotBeNull();
    }

    [Test]
    public async Task Session_ZeroCircuits_RevokedAtExpiry()
    {
        var timeProvider = new FakeTimeProvider(_epoch);
        var coordinator = CreateCoordinator(timeProvider: timeProvider, sessionLifetime: TimeSpan.FromHours(8));
        var record = coordinator.CreateSession(CreateIdentity());

        timeProvider.Advance(TimeSpan.FromHours(8));

        record.State.Should().Be(SessionState.Revoked);
    }

    // ── Cleanup/event invoked once ───────────────────────────────────────────

    [Test]
    public async Task RevokeSessionAsync_EventFiredOnce()
    {
        var coordinator = CreateCoordinator();
        var record = coordinator.CreateSession(CreateIdentity());
        int eventCount = 0;
        coordinator.SessionRevoked += _ => eventCount++;

        await coordinator.RevokeSessionAsync(record.SessionId);
        await coordinator.RevokeSessionAsync(record.SessionId); // Idempotent

        eventCount.Should().Be(1);
    }

    // ── Concurrent callers await same task ───────────────────────────────────

    [Test]
    public async Task RevokeSessionAsync_ConcurrentCallers_ShareSameTask()
    {
        var coordinator = CreateCoordinator();
        var record = coordinator.CreateSession(CreateIdentity());
        var gate = new RevocableSessionActivityGate();
        // Use a non-completed teardown task so revocation stays in-flight
        // when the second caller arrives.
        var teardownTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var teardown = Substitute.For<ICircuitTeardownHandler>();
        teardown.TeardownAsync(Arg.Any<Func<bool>>(), Arg.Any<CancellationToken>()).Returns(teardownTcs.Task);
        var lease = CreateBoundLease(record, "c1", gate, teardown);
        coordinator.ValidateAndRegister(record.SessionId, "https://idp.example.com", "user-123", lease);

        int eventCount = 0;
        coordinator.SessionRevoked += _ => Interlocked.Increment(ref eventCount);

        var task1 = coordinator.RevokeSessionAsync(record.SessionId);
        var task2 = coordinator.RevokeSessionAsync(record.SessionId);

        // Both should return the same task
        task1.Should().BeSameAs(task2);

        // Release the teardown so revocation can complete
        teardownTcs.SetResult();
        await Task.WhenAll(task1, task2);

        record.State.Should().Be(SessionState.Revoked);
        gate.IsActive.Should().BeFalse();
        eventCount.Should().Be(1);
    }

    // ── No retained completed entry after synchronous revocation ────────────

    [Test]
    public async Task RevokeSessionAsync_ZeroCircuit_NoRetainedCompletedEntry()
    {
        var coordinator = CreateCoordinator();
        var record = coordinator.CreateSession(CreateIdentity());
        // No circuits registered → revocation completes synchronously

        var firstCall = coordinator.RevokeSessionAsync(record.SessionId);
        await firstCall;

        // A second call must NOT return the same completed task object,
        // which would indicate a leaked entry in _pendingRevocations.
        var secondCall = coordinator.RevokeSessionAsync(record.SessionId);
        secondCall.Should().NotBeSameAs(firstCall);
    }

    [Test]
    public async Task RevokeSessionAsync_SynchronousTeardown_NoRetainedCompletedEntry()
    {
        var coordinator = CreateCoordinator();
        var record = coordinator.CreateSession(CreateIdentity());
        // Default NSubstitute mock returns Task.CompletedTask → synchronous completion
        var teardown = Substitute.For<ICircuitTeardownHandler>();
        coordinator.ValidateAndRegister(record.SessionId, "https://idp.example.com", "user-123",
            CreateBoundLease(record, "c1", new RevocableSessionActivityGate(), teardown));

        var firstCall = coordinator.RevokeSessionAsync(record.SessionId);
        await firstCall;

        var secondCall = coordinator.RevokeSessionAsync(record.SessionId);
        secondCall.Should().NotBeSameAs(firstCall);
    }

    // ── Registry cleanup completes before TCS signals ──────────────────────

    [Test]
    public async Task RevokeSessionAsync_AsyncTeardown_RegistryCleanedBeforeAwaitResumes()
    {
        var coordinator = CreateCoordinator();
        var record = coordinator.CreateSession(CreateIdentity());
        var teardownTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var teardown = Substitute.For<ICircuitTeardownHandler>();
        teardown.TeardownAsync(Arg.Any<Func<bool>>(), Arg.Any<CancellationToken>()).Returns(teardownTcs.Task);
        coordinator.ValidateAndRegister(record.SessionId, "https://idp.example.com", "user-123",
            CreateBoundLease(record, "c1", new RevocableSessionActivityGate(), teardown));

        var revokeTask = coordinator.RevokeSessionAsync(record.SessionId);

        // Release the async teardown so revocation can complete
        teardownTcs.SetResult();
        await revokeTask;

        // After await, registry must already be clean
        coordinator.GetSession(record.SessionId).Should().BeNull();

        // Second call must not return the same completed task
        var secondCall = coordinator.RevokeSessionAsync(record.SessionId);
        secondCall.Should().NotBeSameAs(revokeTask);
    }

    [Test]
    public async Task RevokeSessionAsync_ZeroCircuit_RegistryCleanedBeforeAwaitResumes()
    {
        var coordinator = CreateCoordinator();
        var record = coordinator.CreateSession(CreateIdentity());

        await coordinator.RevokeSessionAsync(record.SessionId);

        // After await, registry must already be clean
        coordinator.GetSession(record.SessionId).Should().BeNull();

        // Second call must return a fresh task, not the prior completed one
        var secondCall = coordinator.RevokeSessionAsync(record.SessionId);
        secondCall.IsCompleted.Should().BeTrue();
        secondCall.Should().NotBeSameAs(coordinator.RevokeSessionAsync(record.SessionId));
    }

    // ── RemoveSession ────────────────────────────────────────────────────────

    [Test]
    public void RemoveSession_RemovesFromRegistry()
    {
        var coordinator = CreateCoordinator();
        var record = coordinator.CreateSession(CreateIdentity());

        coordinator.RemoveSession(record.SessionId);

        coordinator.GetSession(record.SessionId).Should().BeNull();
    }

    // ── FindSessionByCircuitId ───────────────────────────────────────────────

    [Test]
    public void FindSessionByCircuitId_ExistingCircuit_ReturnsSession()
    {
        var coordinator = CreateCoordinator();
        var record = coordinator.CreateSession(CreateIdentity());
        var lease = CreateBoundLease(record, "circuit-1");
        coordinator.ValidateAndRegister(record.SessionId, "https://idp.example.com", "user-123", lease);

        var found = coordinator.FindSessionByCircuitId("circuit-1");

        found.Should().BeSameAs(record);
    }

    [Test]
    public void FindSessionByCircuitId_UnknownCircuit_ReturnsNull()
    {
        var coordinator = CreateCoordinator();

        var found = coordinator.FindSessionByCircuitId("unknown");

        found.Should().BeNull();
    }

    // ── Timer expiry tears down every lease ──────────────────────────────────

    [Test]
    public async Task TimerExpiry_TearsDownEveryLease()
    {
        var timeProvider = new FakeTimeProvider(_epoch);
        var coordinator = new AppSessionCoordinator(timeProvider, TimeSpan.FromHours(8));
        var identity = CreateIdentity();
        var record = coordinator.CreateSession(identity);

        var teardown1 = Substitute.For<ICircuitTeardownHandler>();
        var teardown2 = Substitute.For<ICircuitTeardownHandler>();
        var gate1 = new RevocableSessionActivityGate();
        var gate2 = new RevocableSessionActivityGate();

        coordinator.ValidateAndRegister(record.SessionId, "https://idp.example.com", "user-123",
            CreateBoundLease(record, "c1", gate1, teardown1));
        coordinator.ValidateAndRegister(record.SessionId, "https://idp.example.com", "user-123",
            CreateBoundLease(record, "c2", gate2, teardown2));

        timeProvider.Advance(TimeSpan.FromHours(8));

        record.State.Should().Be(SessionState.Revoked);
        gate1.IsActive.Should().BeFalse();
        gate2.IsActive.Should().BeFalse();
        await teardown1.Received(1).TeardownAsync(Arg.Any<Func<bool>>(), Arg.Any<CancellationToken>());
        await teardown2.Received(1).TeardownAsync(Arg.Any<Func<bool>>(), Arg.Any<CancellationToken>());
    }

    // ── Timer expiry removes registry entry ──────────────────────────────────

    [Test]
    public async Task TimerExpiry_RemovesRegistryEntry()
    {
        var timeProvider = new FakeTimeProvider(_epoch);
        var coordinator = new AppSessionCoordinator(timeProvider, TimeSpan.FromHours(8));
        var record = coordinator.CreateSession(CreateIdentity());

        timeProvider.Advance(TimeSpan.FromHours(8));

        coordinator.GetSession(record.SessionId).Should().BeNull();
    }

    // ── Timer expiry disposes timer ──────────────────────────────────────────

    [Test]
    public async Task TimerExpiry_DisposesTimer()
    {
        var timeProvider = new FakeTimeProvider(_epoch);
        var coordinator = new AppSessionCoordinator(timeProvider, TimeSpan.FromHours(8));
        var record = coordinator.CreateSession(CreateIdentity());

        // Timer should be created
        // After expiry, timer should be disposed (no way to directly test, but session is removed)
        timeProvider.Advance(TimeSpan.FromHours(8));

        coordinator.GetSession(record.SessionId).Should().BeNull();
    }

    // ── Anonymous/navigation through lease handlers ──────────────────────────

    [Test]
    public async Task RevokeSessionAsync_InvokesTeardownHandlers()
    {
        var coordinator = CreateCoordinator();
        var record = coordinator.CreateSession(CreateIdentity());
        var authInvalidator = Substitute.For<IAuthenticationStateInvalidator>();
        var loginNotifier = Substitute.For<ILoginNavigationNotifier>();
        var logger = Substitute.For<Microsoft.Extensions.Logging.ILogger<ScopedCircuitTeardownHandler>>();
        var teardownHandler = new ScopedCircuitTeardownHandler(
            null, null, authInvalidator, loginNotifier, logger);
        var gate = new RevocableSessionActivityGate();
        var lease = new CircuitLease(gate, teardownHandler);
        lease.TryBind("c1", record);
        coordinator.ValidateAndRegister(record.SessionId, "https://idp.example.com", "user-123", lease);

        await coordinator.RevokeSessionAsync(record.SessionId);

        authInvalidator.Received(1).SetAnonymous();
        loginNotifier.Received(1).NotifyForceLogin();
    }

    // ── Explicit logout revocation skips force-login navigation ─────────────

    [Test]
    public async Task RevokeSessionAsync_NotifyForceLoginDisabled_SetsAnonymousWithoutNavigation()
    {
        var coordinator = CreateCoordinator();
        var record = coordinator.CreateSession(CreateIdentity());
        var authInvalidator = Substitute.For<IAuthenticationStateInvalidator>();
        var loginNotifier = Substitute.For<ILoginNavigationNotifier>();
        var logger = Substitute.For<Microsoft.Extensions.Logging.ILogger<ScopedCircuitTeardownHandler>>();
        var teardownHandler = new ScopedCircuitTeardownHandler(
            null, null, authInvalidator, loginNotifier, logger);
        var gate = new RevocableSessionActivityGate();
        var lease = new CircuitLease(gate, teardownHandler);
        lease.TryBind("c1", record);
        coordinator.ValidateAndRegister(record.SessionId, "https://idp.example.com", "user-123", lease);

        await coordinator.RevokeSessionAsync(record.SessionId, notifyForceLogin: false);

        record.State.Should().Be(SessionState.Revoked);
        gate.IsActive.Should().BeFalse();
        coordinator.GetSession(record.SessionId).Should().BeNull();
        authInvalidator.Received(1).SetAnonymous();
        loginNotifier.DidNotReceive().NotifyForceLogin();
    }

    // ── Expiry already tearing down when explicit logout arrives ────────────

    [Test]
    public async Task ExpiryRevocationInFlight_ExplicitLogoutBeforeNotification_SuppressesForceLogin()
    {
        var timeProvider = new FakeTimeProvider(_epoch);
        var coordinator = new AppSessionCoordinator(timeProvider, TimeSpan.FromHours(8));
        var record = coordinator.CreateSession(CreateIdentity());

        var authInvalidator = Substitute.For<IAuthenticationStateInvalidator>();
        var loginNotifier = Substitute.For<ILoginNavigationNotifier>();
        var logger = Substitute.For<Microsoft.Extensions.Logging.ILogger<ScopedCircuitTeardownHandler>>();

        // Block teardown inside MQTT stop so logout lands mid-revocation.
        var mqttStopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mqttStopRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mqtt = Substitute.For<IMqttManagedClient>();
        mqtt.StopAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            mqttStopEntered.TrySetResult();
            return mqttStopRelease.Task;
        });

        var teardownHandler = new ScopedCircuitTeardownHandler(
            mqtt, null, authInvalidator, loginNotifier, logger);
        var gate = new RevocableSessionActivityGate();
        var lease = new CircuitLease(gate, teardownHandler);
        lease.TryBind("c1", record);
        coordinator.ValidateAndRegister(record.SessionId, "https://idp.example.com", "user-123", lease)
            .Should().NotBeNull();

        // Expiry fires first: revocation starts with force-login allowed.
        timeProvider.Advance(TimeSpan.FromHours(8));
        await mqttStopEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Explicit logout arrives while that revocation is still tearing down.
        var logoutTask = coordinator.RevokeSessionAsync(record.SessionId, notifyForceLogin: false);
        logoutTask.IsCompleted.Should().BeFalse("logout must attach to the in-flight revocation");

        mqttStopRelease.SetResult();
        await logoutTask.WaitAsync(TimeSpan.FromSeconds(5));

        // Cleanup and anonymous transition still happen; only the forced
        // /Login navigation must not, or it steals the OIDC redirect.
        record.State.Should().Be(SessionState.Revoked);
        gate.IsActive.Should().BeFalse();
        coordinator.GetSession(record.SessionId).Should().BeNull();
        authInvalidator.Received(1).SetAnonymous();
        loginNotifier.DidNotReceive().NotifyForceLogin();
    }

    // ── Logout vs timer race ─────────────────────────────────────────────────

    [Test]
    public async Task LogoutTimerRace_BothPathsRevoke_Idempotent()
    {
        var timeProvider = new FakeTimeProvider(_epoch);
        var coordinator = new AppSessionCoordinator(timeProvider, TimeSpan.FromHours(8));
        var identity = CreateIdentity();
        var record = coordinator.CreateSession(identity);
        var gate = new RevocableSessionActivityGate();
        var lease = CreateBoundLease(record, "c1", gate);
        coordinator.ValidateAndRegister(record.SessionId, "https://idp.example.com", "user-123", lease);

        int eventCount = 0;
        coordinator.SessionRevoked += _ => Interlocked.Increment(ref eventCount);

        // Simulate logout and timer firing concurrently
        var logoutTask = Task.Run(async () =>
        {
            await coordinator.RevokeSessionAsync(record.SessionId);
        });

        var timerTask = Task.Run(() =>
        {
            timeProvider.Advance(TimeSpan.FromHours(8));
        });

        await Task.WhenAll(logoutTask, timerTask);

        record.State.Should().Be(SessionState.Revoked);
        gate.IsActive.Should().BeFalse();
        eventCount.Should().Be(1);
    }
}
