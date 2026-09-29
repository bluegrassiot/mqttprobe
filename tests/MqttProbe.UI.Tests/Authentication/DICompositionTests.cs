using Microsoft.Extensions.Time.Testing;
using MqttProbe.Core.Services.Emulation;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Web.Authentication;
using NSubstitute;

namespace MqttProbe.UI.Tests.Authentication;

[TestFixture]
public class DICompositionTests
{
    private static readonly DateTimeOffset _epoch = new(2026, 8, 29, 8, 0, 0, TimeSpan.Zero);

    // ── Coordinator revokes same gate instance injected into scoped deps ─────

    [Test]
    public async Task CoordinatorRevoke_RevokesSameGateInstanceInjectedIntoScopedServices()
    {
        var timeProvider = new FakeTimeProvider(_epoch);
        var coordinator = new AppSessionCoordinator(timeProvider, TimeSpan.FromHours(8));
        var identity = ExternalIdentity.Create("https://idp.example.com", "user-123", "John Doe");
        var record = coordinator.CreateSession(identity);

        // Create the scoped gate that would be injected into MQTT/emulation
        var scopedGate = new RevocableSessionActivityGate();

        // Create scoped teardown handler with the same gate
        var mqttClient = Substitute.For<IMqttManagedClient>();
        var emulationService = Substitute.For<IEmulationService>();
        var authInvalidator = Substitute.For<IAuthenticationStateInvalidator>();
        var loginNotifier = Substitute.For<ILoginNavigationNotifier>();
        var logger = Substitute.For<Microsoft.Extensions.Logging.ILogger<ScopedCircuitTeardownHandler>>();
        var teardownHandler = new ScopedCircuitTeardownHandler(
            mqttClient, emulationService, authInvalidator, loginNotifier, logger);

        // Create lease with the scoped gate and bind it
        var lease = new CircuitLease(scopedGate, teardownHandler);
        lease.TryBind("c1", record);

        // Register into coordinator
        coordinator.ValidateAndRegister(record.SessionId, "https://idp.example.com", "user-123", lease);

        // Revoke via coordinator (simulating session expiry)
        await coordinator.RevokeSessionAsync(record.SessionId);

        // The exact same gate instance should be revoked
        scopedGate.IsActive.Should().BeFalse();

        // The gate's RevocationToken should be cancelled
        scopedGate.RevocationToken.IsCancellationRequested.Should().BeTrue();
    }

    // ── Scoped lease teardown invokes both cleanup dependencies ──────────────

    [Test]
    public async Task ScopedLeaseTeardown_InvokesBothCleanupDependencies()
    {
        var timeProvider = new FakeTimeProvider(_epoch);
        var coordinator = new AppSessionCoordinator(timeProvider, TimeSpan.FromHours(8));
        var identity = ExternalIdentity.Create("https://idp.example.com", "user-123", "John Doe");
        var record = coordinator.CreateSession(identity);

        var scopedGate = new RevocableSessionActivityGate();
        var mqttClient = Substitute.For<IMqttManagedClient>();
        var emulationService = Substitute.For<IEmulationService>();
        var authInvalidator = Substitute.For<IAuthenticationStateInvalidator>();
        var loginNotifier = Substitute.For<ILoginNavigationNotifier>();
        var logger = Substitute.For<Microsoft.Extensions.Logging.ILogger<ScopedCircuitTeardownHandler>>();
        var teardownHandler = new ScopedCircuitTeardownHandler(
            mqttClient, emulationService, authInvalidator, loginNotifier, logger);

        var lease = new CircuitLease(scopedGate, teardownHandler);
        lease.TryBind("c1", record);

        // Teardown the lease
        await lease.TeardownAsync();

        // Both cleanup dependencies were invoked
        await emulationService.Received(1).StopAsync();
        await mqttClient.Received(1).StopAsync(Arg.Any<CancellationToken>());

        // Auth state was invalidated and login was notified
        authInvalidator.Received(1).SetAnonymous();
        loginNotifier.Received(1).NotifyForceLogin();

        // Gate was revoked
        scopedGate.IsActive.Should().BeFalse();
    }

    // ── Gate revocation propagates to MQTT/emulation via RevocationToken ─────

    [Test]
    public void GateRevocation_CancelsRevocationToken_UsedByScopedServices()
    {
        var scopedGate = new RevocableSessionActivityGate();
        var cancellationToken = scopedGate.RevocationToken;

        cancellationToken.IsCancellationRequested.Should().BeFalse();

        scopedGate.Revoke();

        cancellationToken.IsCancellationRequested.Should().BeTrue();
    }
}
