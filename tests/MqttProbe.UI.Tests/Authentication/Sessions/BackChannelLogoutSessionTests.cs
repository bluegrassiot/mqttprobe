using Microsoft.Extensions.Time.Testing;
using MqttProbe.Core.Services.Emulation;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Web.Authentication;

namespace MqttProbe.UI.Tests.Authentication;

[TestFixture]
public class BackChannelLogoutSessionTests
{
    private static readonly DateTimeOffset _epoch = new(2026, 8, 29, 8, 0, 0, TimeSpan.Zero);

    private const string Issuer = "https://idp.example.com";
    private const string OtherIssuer = "https://other-idp.example.com";

    private static AppSessionCoordinator CreateCoordinator(TimeProvider? timeProvider = null)
        => new(timeProvider ?? new FakeTimeProvider(_epoch), TimeSpan.FromHours(8));

    private static AppSessionRecord CreateSession(
        AppSessionCoordinator coordinator,
        string subject = "user-123",
        string? sid = null,
        string issuer = Issuer)
        => coordinator.CreateSession(ExternalIdentity.Create(issuer, subject, "User"), sid);

    // ── sid capture at login ────────────────────────────────────────────────

    [Test]
    public void CreateSession_WithSid_CapturesProviderSession()
    {
        var coordinator = CreateCoordinator();

        var record = CreateSession(coordinator, sid: "sid-1");

        record.Sid.Should().Be("sid-1");
    }

    [Test]
    public void CreateSession_WithoutSid_LeavesProviderSessionNull()
    {
        var coordinator = CreateCoordinator();

        var record = CreateSession(coordinator);

        record.Sid.Should().BeNull();
    }

    // ── Matching rules ──────────────────────────────────────────────────────

    [Test]
    public void FindSessionsForLogout_MatchingSidAndSubject_ReturnsThatSession()
    {
        var coordinator = CreateCoordinator();
        var target = CreateSession(coordinator, sid: "sid-1");
        var sibling = CreateSession(coordinator, sid: "sid-2");

        var sessions = coordinator.FindSessionsForLogout(Issuer, "sid-1", "user-123");

        sessions.Should().ContainSingle().Which.Should().BeSameAs(target);
        coordinator.GetSession(sibling.SessionId).Should().NotBeNull();
    }

    [Test]
    public void FindSessionsForLogout_MismatchedSid_DoesNotFallBackToSubject()
    {
        var coordinator = CreateCoordinator();
        var record = CreateSession(coordinator, sid: "sid-2");

        var sessions = coordinator.FindSessionsForLogout(Issuer, "sid-1", "user-123");

        sessions.Should().BeEmpty();
        coordinator.GetSession(record.SessionId).Should().NotBeNull();
    }

    [Test]
    public void FindSessionsForLogout_TokenSidWithSidlessSession_DoesNotMatch()
    {
        var coordinator = CreateCoordinator();
        var record = CreateSession(coordinator, sid: null);

        var sessions = coordinator.FindSessionsForLogout(Issuer, "sid-1", "user-123");

        sessions.Should().BeEmpty();
        coordinator.GetSession(record.SessionId).Should().NotBeNull();
    }

    [Test]
    public void FindSessionsForLogout_SidMatchesButSubjectDiffers_DoesNotMatch()
    {
        var coordinator = CreateCoordinator();
        var record = CreateSession(coordinator, subject: "user-123", sid: "sid-1");

        var sessions = coordinator.FindSessionsForLogout(Issuer, "sid-1", "someone-else");

        sessions.Should().BeEmpty();
        coordinator.GetSession(record.SessionId).Should().NotBeNull();
    }

    [Test]
    public void FindSessionsForLogout_TokenWithoutSid_MatchesIssuerAndSubject()
    {
        var coordinator = CreateCoordinator();
        var first = CreateSession(coordinator, sid: "sid-1");
        var second = CreateSession(coordinator, sid: "sid-2");
        var other = CreateSession(coordinator, subject: "user-456", sid: "sid-3");

        var sessions = coordinator.FindSessionsForLogout(Issuer, null, "user-123");

        sessions.Should().HaveCount(2).And.Contain(first).And.Contain(second);
        coordinator.GetSession(other.SessionId).Should().NotBeNull();
    }

    [Test]
    public void FindSessionsForLogout_MatchingSidFromDifferentIssuer_DoesNotMatch()
    {
        var coordinator = CreateCoordinator();
        var record = CreateSession(coordinator, issuer: OtherIssuer, sid: "sid-1");

        var sessions = coordinator.FindSessionsForLogout(Issuer, "sid-1", "user-123");

        sessions.Should().BeEmpty();
        coordinator.GetSession(record.SessionId).Should().NotBeNull();
    }

    [Test]
    public void FindSessionsForLogout_WithoutSubOrSid_MatchesNothing()
    {
        var coordinator = CreateCoordinator();
        CreateSession(coordinator, sid: "sid-1");

        coordinator.FindSessionsForLogout(Issuer, null, null).Should().BeEmpty();
    }

    // ── Revocation goes through the existing teardown path ───────────────────

    [Test]
    public async Task RevokeMatchedSession_TearsDownBoundLeaseOutsideLock()
    {
        var coordinator = CreateCoordinator();
        var record = CreateSession(coordinator, sid: "sid-1");

        var scopedGate = new RevocableSessionActivityGate();
        var teardownHandler = new ScopedCircuitTeardownHandler(
            Substitute.For<IMqttManagedClient>(),
            Substitute.For<IEmulationService>(),
            Substitute.For<IAuthenticationStateInvalidator>(),
            Substitute.For<ILoginNavigationNotifier>(),
            Substitute.For<Microsoft.Extensions.Logging.ILogger<ScopedCircuitTeardownHandler>>());
        var lease = new CircuitLease(scopedGate, teardownHandler);
        lease.TryBind("c1", record);
        coordinator.ValidateAndRegister(record.SessionId, Issuer, "user-123", lease);

        var sessions = coordinator.FindSessionsForLogout(Issuer, "sid-1", "user-123");
        foreach (var session in sessions)
        {
            await coordinator.RevokeSessionAsync(session.SessionId);
        }

        scopedGate.IsActive.Should().BeFalse();
        coordinator.GetSession(record.SessionId).Should().BeNull();
        record.State.Should().Be(Web.Authentication.SessionState.Revoked);
    }
}
