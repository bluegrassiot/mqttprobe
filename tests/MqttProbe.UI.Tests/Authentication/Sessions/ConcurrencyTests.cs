using Microsoft.Extensions.Time.Testing;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Web.Authentication;
using NSubstitute;
using SessionState = MqttProbe.Web.Authentication.SessionState;

namespace MqttProbe.UI.Tests.Authentication;

[TestFixture]
public class ConcurrencyTests
{
    private static readonly DateTimeOffset _epoch = new(2026, 8, 29, 8, 0, 0, TimeSpan.Zero);

    private static CircuitLease CreateBoundLease(
        AppSessionRecord record,
        string circuitId = "c1",
        RevocableSessionActivityGate? gate = null,
        ICircuitTeardownHandler? teardown = null)
    {
        var lease = new CircuitLease(
            gate ?? new RevocableSessionActivityGate(),
            teardown ?? Substitute.For<ICircuitTeardownHandler>());
        lease.TryBind(circuitId, record);
        return lease;
    }

    // ── True register/revoke race ────────────────────────────────────────────

    [Test]
    public async Task RegisterRevokeRace_ConcurrentAccess_DoesNotCorruptState()
    {
        var coordinator = new AppSessionCoordinator(new FakeTimeProvider(_epoch), TimeSpan.FromHours(8));
        var identity = ExternalIdentity.Create("https://idp.example.com", "user-123", "John Doe");
        var record = coordinator.CreateSession(identity);

        var barrier = new Barrier(2);
        var registeredLeases = new System.Collections.Concurrent.ConcurrentBag<CircuitLease?>();
        var revoked = false;

        var registerTask = Task.Run(() =>
        {
            barrier.SignalAndWait();
            for (int i = 0; i < 100; i++)
            {
                var lease = CreateBoundLease(record, $"c-{i}");
                var result = coordinator.ValidateAndRegister(
                    record.SessionId, "https://idp.example.com", "user-123", lease);
                registeredLeases.Add(result);
            }
        });

        var revokeTask = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            await coordinator.RevokeSessionAsync(record.SessionId);
            revoked = true;
        });

        await Task.WhenAll(registerTask, revokeTask);

        // After revoke, no more registrations should succeed
        revoked.Should().BeTrue();
        record.State.Should().Be(SessionState.Revoked);

        // All registered leases should have revoked gates
        foreach (var lease in registeredLeases.Where(l => l is not null))
        {
            lease!.Gate.IsActive.Should().BeFalse();
        }
    }

    // ── Concurrent register from multiple circuits ───────────────────────────

    [Test]
    public async Task ConcurrentRegister_MultipleCircuits_AllRegistered()
    {
        var coordinator = new AppSessionCoordinator(new FakeTimeProvider(_epoch), TimeSpan.FromHours(8));
        var identity = ExternalIdentity.Create("https://idp.example.com", "user-123", "John Doe");
        var record = coordinator.CreateSession(identity);

        var tasks = Enumerable.Range(0, 10).Select(i => Task.Run(() =>
        {
            var lease = CreateBoundLease(record, $"c-{i}");
            return coordinator.ValidateAndRegister(
                record.SessionId, "https://idp.example.com", "user-123", lease);
        })).ToArray();

        var results = await Task.WhenAll(tasks);

        results.Should().AllSatisfy(r => r.Should().NotBeNull());
        record.CircuitCount.Should().Be(10);
    }

    // ── Concurrent revoke from multiple sources ──────────────────────────────

    [Test]
    public async Task ConcurrentRevoke_MultipleSources_Idempotent()
    {
        var coordinator = new AppSessionCoordinator(new FakeTimeProvider(_epoch), TimeSpan.FromHours(8));
        var identity = ExternalIdentity.Create("https://idp.example.com", "user-123", "John Doe");
        var record = coordinator.CreateSession(identity);
        var gate = new RevocableSessionActivityGate();
        var lease = CreateBoundLease(record, "c1", gate);
        coordinator.ValidateAndRegister(record.SessionId, "https://idp.example.com", "user-123", lease);

        int eventCount = 0;
        coordinator.SessionRevoked += _ => Interlocked.Increment(ref eventCount);

        var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(async () =>
        {
            await coordinator.RevokeSessionAsync(record.SessionId);
        })).ToArray();

        await Task.WhenAll(tasks);

        record.State.Should().Be(SessionState.Revoked);
        gate.IsActive.Should().BeFalse();
        eventCount.Should().Be(1);
    }

    // ── Concurrent teardown ──────────────────────────────────────────────────

    [Test]
    public async Task ConcurrentTeardown_Idempotent()
    {
        var coordinator = new AppSessionCoordinator(new FakeTimeProvider(_epoch), TimeSpan.FromHours(8));
        var identity = ExternalIdentity.Create("https://idp.example.com", "user-123", "John Doe");
        var record = coordinator.CreateSession(identity);
        var gate = new RevocableSessionActivityGate();
        var teardown = Substitute.For<ICircuitTeardownHandler>();
        var lease = CreateBoundLease(record, "c1", gate, teardown);

        var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
        {
            return lease.TeardownAsync();
        })).ToArray();

        await Task.WhenAll(tasks);

        gate.IsActive.Should().BeFalse();
        await teardown.Received(1).TeardownAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    // ── Logout vs timer race ─────────────────────────────────────────────────

    [Test]
    public async Task LogoutTimerRace_BothPathsRevoke_Idempotent()
    {
        var timeProvider = new FakeTimeProvider(_epoch);
        var coordinator = new AppSessionCoordinator(timeProvider, TimeSpan.FromHours(8));
        var identity = ExternalIdentity.Create("https://idp.example.com", "user-123", "John Doe");
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
