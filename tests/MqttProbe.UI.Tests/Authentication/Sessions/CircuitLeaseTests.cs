using Microsoft.Extensions.Time.Testing;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Web.Authentication;

namespace MqttProbe.UI.Tests.Authentication;

[TestFixture]
public class CircuitLeaseTests
{
    private static readonly DateTimeOffset _epoch = new(2026, 8, 29, 8, 0, 0, TimeSpan.Zero);

    private static CircuitLease CreateLease(
        RevocableSessionActivityGate? gate = null,
        ICircuitTeardownHandler? teardown = null)
    {
        return new CircuitLease(
            gate ?? new RevocableSessionActivityGate(),
            teardown ?? Substitute.For<ICircuitTeardownHandler>());
    }

    // ── Teardown is idempotent ───────────────────────────────────────────────

    [Test]
    public async Task TeardownAsync_CalledTwice_OnlyTearsDownOnce()
    {
        var coordinator = new AppSessionCoordinator(new FakeTimeProvider(_epoch), TimeSpan.FromHours(8));
        var identity = ExternalIdentity.Create("https://idp.example.com", "user-123", "John Doe");
        coordinator.CreateSession(identity);
        var gate = new RevocableSessionActivityGate();
        var teardown = Substitute.For<ICircuitTeardownHandler>();
        var lease = CreateLease(gate, teardown);

        await lease.TeardownAsync();
        await lease.TeardownAsync();

        await teardown.Received(1).TeardownAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    // ── Navigation flag reaches the handler ──────────────────────────────────

    [Test]
    public async Task TeardownAsync_NotifyForceLoginDisabled_PassesFlagToHandler()
    {
        var gate = new RevocableSessionActivityGate();
        var teardown = Substitute.For<ICircuitTeardownHandler>();
        var lease = CreateLease(gate, teardown);

        await lease.TeardownAsync(notifyForceLogin: false);

        gate.IsActive.Should().BeFalse();
        await teardown.Received(1).TeardownAsync(false, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TeardownAsync_Default_NotifiesForceLogin()
    {
        var teardown = Substitute.For<ICircuitTeardownHandler>();
        var lease = CreateLease(teardown: teardown);

        await lease.TeardownAsync();

        await teardown.Received(1).TeardownAsync(true, Arg.Any<CancellationToken>());
    }

    // ── Teardown revokes gate ────────────────────────────────────────────────

    [Test]
    public async Task TeardownAsync_RevokesGate()
    {
        var coordinator = new AppSessionCoordinator(new FakeTimeProvider(_epoch), TimeSpan.FromHours(8));
        var identity = ExternalIdentity.Create("https://idp.example.com", "user-123", "John Doe");
        coordinator.CreateSession(identity);
        var gate = new RevocableSessionActivityGate();
        var teardown = Substitute.For<ICircuitTeardownHandler>();
        var lease = CreateLease(gate, teardown);

        await lease.TeardownAsync();

        gate.IsActive.Should().BeFalse();
    }

    [Test]
    public async Task TeardownAsync_RevocationCallbackBlocked_TeardownStillCompletes()
    {
        var gate = new RevocableSessionActivityGate();
        var release = new ManualResetEventSlim(false);
        var teardown = Substitute.For<ICircuitTeardownHandler>();
        var lease = CreateLease(gate, teardown);

        try
        {
            gate.RevocationToken.Register(() => release.Wait(TimeSpan.FromSeconds(15)));

            await lease.TeardownAsync().WaitAsync(TimeSpan.FromSeconds(5));

            gate.IsActive.Should().BeFalse();
            await teardown.Received(1).TeardownAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            release.Set();
        }

        await gate.RevocationCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        release.Dispose();
    }

    // ── Gate is the exact scoped instance ────────────────────────────────────

    [Test]
    public void Lease_OwnsExactScopedGate()
    {
        var coordinator = new AppSessionCoordinator(new FakeTimeProvider(_epoch), TimeSpan.FromHours(8));
        var identity = ExternalIdentity.Create("https://idp.example.com", "user-123", "John Doe");
        coordinator.CreateSession(identity);
        var gate = new RevocableSessionActivityGate();
        var teardown = Substitute.For<ICircuitTeardownHandler>();
        var lease = CreateLease(gate, teardown);

        lease.Gate.Should().BeSameAs(gate);
    }

    // ── Sibling circuit unaffected ───────────────────────────────────────────

    [Test]
    public async Task TeardownAsync_SiblingCircuitUnaffected()
    {
        var coordinator = new AppSessionCoordinator(new FakeTimeProvider(_epoch), TimeSpan.FromHours(8));
        var identity = ExternalIdentity.Create("https://idp.example.com", "user-123", "John Doe");
        var record = coordinator.CreateSession(identity);
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

        await lease1.TeardownAsync();

        gate1.IsActive.Should().BeFalse();
        gate2.IsActive.Should().BeTrue();
        await teardown2.DidNotReceive().TeardownAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    // ── Binding ──────────────────────────────────────────────────────────────

    [Test]
    public void TryBind_FirstBind_ReturnsTrue()
    {
        var lease = CreateLease();
        var coordinator = new AppSessionCoordinator(new FakeTimeProvider(_epoch), TimeSpan.FromHours(8));
        var record = coordinator.CreateSession(ExternalIdentity.Create("https://idp.example.com", "user-123", "John Doe"));

        var result = lease.TryBind("c1", record);

        result.Should().BeTrue();
        lease.IsBound.Should().BeTrue();
        lease.CircuitId.Should().Be("c1");
    }

    [Test]
    public void TryBind_SecondBind_ReturnsFalse()
    {
        var lease = CreateLease();
        var coordinator = new AppSessionCoordinator(new FakeTimeProvider(_epoch), TimeSpan.FromHours(8));
        var record = coordinator.CreateSession(ExternalIdentity.Create("https://idp.example.com", "user-123", "John Doe"));

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
}
