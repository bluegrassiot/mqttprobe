using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Web.Authentication;
using SessionState = MqttProbe.Web.Authentication.SessionState;

namespace MqttProbe.UI.Tests.Authentication;

[TestFixture]
public class AppSessionRecordTests
{
    private static readonly DateTimeOffset _epoch = new(2026, 8, 29, 8, 0, 0, TimeSpan.Zero);

    private static AppSessionCoordinator CreateCoordinator()
    {
        return new AppSessionCoordinator(new FakeTimeProvider(_epoch), TimeSpan.FromHours(8));
    }

    private static ExternalIdentity CreateIdentity() =>
        ExternalIdentity.Create("https://idp.example.com", "user-123", "John Doe");

    private static CircuitLease CreateLease()
    {
        return new CircuitLease(
            new RevocableSessionActivityGate(),
            Substitute.For<ICircuitTeardownHandler>());
    }

    private static CircuitLease CreateBoundLease(AppSessionRecord record, string circuitId = "c1")
    {
        var lease = CreateLease();
        lease.TryBind(circuitId, record).Should().BeTrue();
        return lease;
    }

    // ── Identity and counters ────────────────────────────────────────────────

    [Test]
    public void NewRecord_ExposesIdentityAndCounters()
    {
        var record = CreateCoordinator().CreateSession(CreateIdentity());

        record.SessionId.Should().NotBeEmpty();
        record.Identity.Should().Be(CreateIdentity());
        record.ExpiresAt.Should().Be(_epoch.AddHours(8));
        record.Issuer.Should().Be("https://idp.example.com");
        record.Subject.Should().Be("user-123");
        record.DisplayName.Should().Be("John Doe");
        record.State.Should().Be(SessionState.Active);
        record.CircuitCount.Should().Be(0);
    }

    // ── IsValid ──────────────────────────────────────────────────────────────

    [Test]
    public void IsValid_AllMatchAndUnexpired_ReturnsTrue()
    {
        var record = CreateCoordinator().CreateSession(CreateIdentity());

        var valid = record.IsValid(
            record.SessionId, "https://idp.example.com", "user-123", _epoch.AddHours(7));

        valid.Should().BeTrue();
    }

    [Test]
    public void IsValid_WrongSessionId_ReturnsFalse()
    {
        var record = CreateCoordinator().CreateSession(CreateIdentity());

        record.IsValid("other", "https://idp.example.com", "user-123", _epoch)
            .Should().BeFalse();
    }

    [Test]
    public void IsValid_WrongIssuer_ReturnsFalse()
    {
        var record = CreateCoordinator().CreateSession(CreateIdentity());

        record.IsValid(record.SessionId, "https://other.example.com", "user-123", _epoch)
            .Should().BeFalse();
    }

    [Test]
    public void IsValid_WrongSubject_ReturnsFalse()
    {
        var record = CreateCoordinator().CreateSession(CreateIdentity());

        record.IsValid(record.SessionId, "https://idp.example.com", "other", _epoch)
            .Should().BeFalse();
    }

    [Test]
    public void IsValid_AtOrAfterExpiry_ReturnsFalse()
    {
        var record = CreateCoordinator().CreateSession(CreateIdentity());

        record.IsValid(record.SessionId, "https://idp.example.com", "user-123", record.ExpiresAt)
            .Should().BeFalse();
    }

    [Test]
    public void IsValid_AfterRevocation_ReturnsFalse()
    {
        var record = CreateCoordinator().CreateSession(CreateIdentity());
        record.Revoke();

        record.IsValid(record.SessionId, "https://idp.example.com", "user-123", _epoch)
            .Should().BeFalse();
        record.State.Should().Be(SessionState.Revoked);
    }

    // ── RegisterCircuit ──────────────────────────────────────────────────────

    [Test]
    public void RegisterCircuit_BoundLease_AddsAndReturnsLease()
    {
        var record = CreateCoordinator().CreateSession(CreateIdentity());
        var lease = CreateBoundLease(record);

        var registered = record.RegisterCircuit(lease);

        registered.Should().BeSameAs(lease);
        record.CircuitCount.Should().Be(1);
        record.ContainsCircuit("c1").Should().BeTrue();
    }

    [Test]
    public void RegisterCircuit_UnboundLease_ReturnsNullAndDoesNotAdd()
    {
        var record = CreateCoordinator().CreateSession(CreateIdentity());
        var unbound = CreateLease();

        var registered = record.RegisterCircuit(unbound);

        registered.Should().BeNull();
        record.CircuitCount.Should().Be(0);
    }

    [Test]
    public void RegisterCircuit_SameCircuitTwice_ReturnsExistingLease()
    {
        var record = CreateCoordinator().CreateSession(CreateIdentity());
        var first = CreateBoundLease(record);
        var second = CreateBoundLease(record);

        record.RegisterCircuit(first).Should().BeSameAs(first);
        var duplicate = record.RegisterCircuit(second);

        duplicate.Should().BeSameAs(first);
        record.CircuitCount.Should().Be(1);
    }

    [Test]
    public void RegisterCircuit_RevokedSession_ReturnsNull()
    {
        var record = CreateCoordinator().CreateSession(CreateIdentity());
        var lease = CreateBoundLease(record);
        record.Revoke();

        record.RegisterCircuit(lease).Should().BeNull();
        record.CircuitCount.Should().Be(0);
    }

    // ── UnregisterCircuit / GetLease / ContainsCircuit ──────────────────────

    [Test]
    public void UnregisterCircuit_RemovesLease()
    {
        var record = CreateCoordinator().CreateSession(CreateIdentity());
        record.RegisterCircuit(CreateBoundLease(record));
        record.CircuitCount.Should().Be(1);

        record.UnregisterCircuit("c1");

        record.CircuitCount.Should().Be(0);
        record.ContainsCircuit("c1").Should().BeFalse();
        record.GetLease("c1").Should().BeNull();
    }

    [Test]
    public void UnregisterCircuit_UnknownCircuit_LeavesLeasesUntouched()
    {
        var record = CreateCoordinator().CreateSession(CreateIdentity());
        record.RegisterCircuit(CreateBoundLease(record));

        record.UnregisterCircuit("nope");

        record.CircuitCount.Should().Be(1);
        record.ContainsCircuit("c1").Should().BeTrue();
    }

    [Test]
    public void GetLease_KnownCircuit_ReturnsBoundLease()
    {
        var record = CreateCoordinator().CreateSession(CreateIdentity());
        var lease = CreateBoundLease(record);
        record.RegisterCircuit(lease);

        var found = record.GetLease("c1");

        found.Should().BeSameAs(lease);
        found.CircuitId.Should().Be("c1");
        found.Session.Should().BeSameAs(record);
    }

    [Test]
    public void GetLease_UnknownCircuit_ReturnsNull()
    {
        var record = CreateCoordinator().CreateSession(CreateIdentity());

        record.GetLease("missing").Should().BeNull();
    }

    // ── Revoke ───────────────────────────────────────────────────────────────

    [Test]
    public void Revoke_ActiveSession_ReturnsLeasesAndRevokesGates()
    {
        var record = CreateCoordinator().CreateSession(CreateIdentity());
        var gate1 = new RevocableSessionActivityGate();
        var gate2 = new RevocableSessionActivityGate();
        var lease1 = new CircuitLease(gate1, Substitute.For<ICircuitTeardownHandler>());
        var lease2 = new CircuitLease(gate2, Substitute.For<ICircuitTeardownHandler>());
        lease1.TryBind("c1", record);
        lease2.TryBind("c2", record);
        record.RegisterCircuit(lease1);
        record.RegisterCircuit(lease2);

        var revoked = record.Revoke();

        revoked.Should().HaveCount(2);
        revoked.Should().BeEquivalentTo(new[] { lease1, lease2 });
        gate1.IsActive.Should().BeFalse();
        gate2.IsActive.Should().BeFalse();
        record.State.Should().Be(SessionState.Revoked);
    }

    [Test]
    public async Task Revoke_ThrowingCallback_StillRevokesEveryGateAndLogsTheFailure()
    {
        var logger = Substitute.For<ILogger<RevocableSessionActivityGate>>();
        var record = CreateCoordinator().CreateSession(CreateIdentity());
        var throwingGate = new RevocableSessionActivityGate(logger);
        var siblingGate = new RevocableSessionActivityGate();
        var throwingLease = new CircuitLease(throwingGate, Substitute.For<ICircuitTeardownHandler>());
        var siblingLease = new CircuitLease(siblingGate, Substitute.For<ICircuitTeardownHandler>());
        throwingLease.TryBind("c1", record);
        siblingLease.TryBind("c2", record);
        record.RegisterCircuit(throwingLease);
        record.RegisterCircuit(siblingLease);
        throwingGate.RevocationToken.Register(() => throw new InvalidOperationException("callback failed"));

        var revoked = record.Revoke();

        // The gate closes and the sibling is revoked without waiting for the
        // callback that is about to throw.
        revoked.Should().BeEquivalentTo(new[] { throwingLease, siblingLease });
        throwingGate.IsActive.Should().BeFalse();
        siblingGate.IsActive.Should().BeFalse();
        record.State.Should().Be(SessionState.Revoked);
        record.AreGatesRevoked.Should().BeTrue();

        await throwingGate.RevocationCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        logger.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(ILogger.Log))
            .Select(call => call.GetArguments()[2]?.ToString() ?? "")
            .Should().Contain(message => message.Contains("revocation callback failed"));
    }

    [Test]
    public void AreGatesRevoked_ActiveSession_ReturnsFalse()
    {
        var record = CreateCoordinator().CreateSession(CreateIdentity());
        record.RegisterCircuit(CreateBoundLease(record));

        record.AreGatesRevoked.Should().BeFalse();
    }

    [Test]
    public void Revoke_Twice_ReturnsEmptyListSecondTime()
    {
        var record = CreateCoordinator().CreateSession(CreateIdentity());
        var lease = CreateBoundLease(record);
        record.RegisterCircuit(lease);

        var first = record.Revoke();
        var second = record.Revoke();

        first.Should().HaveCount(1);
        second.Should().BeEmpty();
        record.State.Should().Be(SessionState.Revoked);
    }

    [Test]
    public void RevokeSessionThroughCoordinator_LeavesRecordRevoked()
    {
        var coordinator = CreateCoordinator();
        var record = coordinator.CreateSession(CreateIdentity());
        var lease = CreateBoundLease(record);
        coordinator.ValidateAndRegister(
            record.SessionId, "https://idp.example.com", "user-123", lease);

        coordinator.RevokeSessionAsync(record.SessionId).Wait();

        record.State.Should().Be(SessionState.Revoked);
        coordinator.GetSession(record.SessionId).Should().BeNull();
    }
}
