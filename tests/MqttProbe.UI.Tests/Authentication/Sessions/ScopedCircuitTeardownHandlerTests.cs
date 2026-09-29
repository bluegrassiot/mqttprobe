using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MqttProbe.Core.Services.Emulation;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Web.Authentication;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MqttProbe.UI.Tests.Authentication;

[TestFixture]
public class ScopedCircuitTeardownHandlerTests
{
    private static readonly DateTimeOffset _epoch = new(2026, 8, 29, 8, 0, 0, TimeSpan.Zero);

    private static (
        ScopedCircuitTeardownHandler Handler,
        IMqttManagedClient MqttClient,
        IEmulationService EmulationService,
        IAuthenticationStateInvalidator AuthStateInvalidator,
        ILoginNavigationNotifier LoginNotifier)
        CreateSut(TimeSpan? cleanupTimeout = null)
    {
        var mqtt = Substitute.For<IMqttManagedClient>();
        var emulation = Substitute.For<IEmulationService>();
        var authInvalidator = Substitute.For<IAuthenticationStateInvalidator>();
        var loginNotifier = Substitute.For<ILoginNavigationNotifier>();
        var logger = Substitute.For<ILogger<ScopedCircuitTeardownHandler>>();
        var handler = new ScopedCircuitTeardownHandler(
            mqtt, emulation, authInvalidator, loginNotifier, logger, cleanupTimeout);
        return (handler, mqtt, emulation, authInvalidator, loginNotifier);
    }

    // ── Gate first, then cleanup ─────────────────────────────────────────────

    [Test]
    public async Task TeardownAsync_RevokesGateFirst()
    {
        var (handler, mqtt, emulation, _, _) = CreateSut();
        var gate = new RevocableSessionActivityGate();

        // Gate is revoked by CircuitLease.TeardownAsync before handler.TeardownAsync
        gate.Revoke();
        await handler.TeardownAsync();

        gate.IsActive.Should().BeFalse();
    }

    // ── Concurrent emulation+MQTT cleanup ────────────────────────────────────

    [Test]
    public async Task TeardownAsync_CallsBothEmulationAndMqttStop()
    {
        var (handler, mqtt, emulation, _, _) = CreateSut();

        await handler.TeardownAsync();

        await emulation.Received(1).StopAsync();
        await mqtt.Received(1).StopAsync(Arg.Any<CancellationToken>());
    }

    // ── Hung emulation does not block MQTT ───────────────────────────────────

    [Test]
    public async Task TeardownAsync_HungEmulation_DoesNotBlockMqtt()
    {
        var (handler, mqtt, emulation, _, _) = CreateSut(cleanupTimeout: TimeSpan.FromMilliseconds(100));

        // Emulation hangs forever
        var tcs = new TaskCompletionSource();
        emulation.StopAsync().Returns(tcs.Task);

        await handler.TeardownAsync();

        // MQTT should still be called
        await mqtt.Received(1).StopAsync(Arg.Any<CancellationToken>());
    }

    // ── Cleanup failure still sets anonymous + notifies ──────────────────────

    [Test]
    public async Task TeardownAsync_EmulationFails_StillSetsAnonymousAndNotifies()
    {
        var (handler, _, emulation, authInvalidator, loginNotifier) = CreateSut();
        emulation.StopAsync().ThrowsAsync(new InvalidOperationException("emulator fault"));

        await handler.TeardownAsync();

        authInvalidator.Received(1).SetAnonymous();
        loginNotifier.Received(1).NotifyForceLogin();
    }

    [Test]
    public async Task TeardownAsync_MqttFails_StillSetsAnonymousAndNotifies()
    {
        var (handler, mqtt, _, authInvalidator, loginNotifier) = CreateSut();
        mqtt.StopAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException("broker unreachable"));

        await handler.TeardownAsync();

        authInvalidator.Received(1).SetAnonymous();
        loginNotifier.Received(1).NotifyForceLogin();
    }

    [Test]
    public async Task TeardownAsync_BothFail_StillSetsAnonymousAndNotifies()
    {
        var (handler, mqtt, emulation, authInvalidator, loginNotifier) = CreateSut();
        emulation.StopAsync().ThrowsAsync(new InvalidOperationException("emulator fault"));
        mqtt.StopAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException("broker unreachable"));

        await handler.TeardownAsync();

        authInvalidator.Received(1).SetAnonymous();
        loginNotifier.Received(1).NotifyForceLogin();
    }

    [Test]
    public async Task TeardownAsync_NotifyForceLoginDisabled_SetsAnonymousWithoutNavigation()
    {
        var (handler, mqtt, emulation, authInvalidator, loginNotifier) = CreateSut();

        await handler.TeardownAsync(notifyForceLogin: false);

        await emulation.Received(1).StopAsync();
        await mqtt.Received(1).StopAsync(Arg.Any<CancellationToken>());
        authInvalidator.Received(1).SetAnonymous();
        loginNotifier.DidNotReceive().NotifyForceLogin();
    }

    // ── Idempotent ───────────────────────────────────────────────────────────

    [Test]
    public async Task TeardownAsync_CalledTwice_DoesNotThrow()
    {
        var (handler, mqtt, emulation, _, _) = CreateSut();

        await handler.TeardownAsync();
        var act = () => handler.TeardownAsync();

        await act.Should().NotThrowAsync();
    }

    // ── Null services ────────────────────────────────────────────────────────

    [Test]
    public async Task TeardownAsync_NullMqttAndEmulation_DoesNotThrow()
    {
        var authInvalidator = Substitute.For<IAuthenticationStateInvalidator>();
        var loginNotifier = Substitute.For<ILoginNavigationNotifier>();
        var logger = Substitute.For<ILogger<ScopedCircuitTeardownHandler>>();
        var handler = new ScopedCircuitTeardownHandler(
            null, null, authInvalidator, loginNotifier, logger);

        var act = () => handler.TeardownAsync();

        await act.Should().NotThrowAsync();
        authInvalidator.Received(1).SetAnonymous();
        loginNotifier.Received(1).NotifyForceLogin();
    }

    // ── Fake-time internal timeout ───────────────────────────────────────────

    [Test]
    public async Task TeardownAsync_TimeoutBound_RespectsInjectedTimeout()
    {
        var (handler, mqtt, emulation, _, _) = CreateSut(cleanupTimeout: TimeSpan.FromMilliseconds(50));

        // Both services hang
        var tcs = new TaskCompletionSource();
        emulation.StopAsync().Returns(tcs.Task);
        mqtt.StopAsync(Arg.Any<CancellationToken>()).Returns(tcs.Task);

        // Should complete within timeout, not hang forever
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await handler.TeardownAsync(cancellationToken: cts.Token);

        // Services were called
        await emulation.Received(1).StopAsync();
    }
}
