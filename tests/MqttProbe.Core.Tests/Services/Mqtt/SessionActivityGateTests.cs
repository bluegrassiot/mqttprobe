using System.Diagnostics;
using Microsoft.Extensions.Logging;
using MqttProbe.Core.Services.Mqtt;

namespace MqttProbe.Core.Tests.Services.Mqtt;

[TestFixture]
public class SessionActivityGateTests
{
    [Test]
    public void AlwaysActiveGate_IsActive_ReturnsTrue()
    {
        var gate = new AlwaysActiveSessionActivityGate();
        gate.IsActive.Should().BeTrue();
    }

    [Test]
    public void AlwaysActiveGate_EnsureActive_DoesNotThrow()
    {
        var gate = new AlwaysActiveSessionActivityGate();
        var act = () => gate.EnsureActive();
        act.Should().NotThrow();
    }

    [Test]
    public void AlwaysActiveGate_RevocationToken_IsCancellationTokenNone()
    {
        var gate = new AlwaysActiveSessionActivityGate();
        gate.RevocationToken.Should().Be(CancellationToken.None);
    }

    [Test]
    public void AlwaysActiveGate_Revoke_DoesNotChangeIsActive()
    {
        var gate = new AlwaysActiveSessionActivityGate();
        gate.Revoke();
        gate.IsActive.Should().BeTrue();
    }

    [Test]
    public void RevocableGate_InitiallyActive()
    {
        var gate = new RevocableSessionActivityGate();
        gate.IsActive.Should().BeTrue();
        gate.RevocationToken.IsCancellationRequested.Should().BeFalse();
    }

    [Test]
    public void RevocableGate_EnsureActive_BeforeRevoke_DoesNotThrow()
    {
        var gate = new RevocableSessionActivityGate();
        var act = () => gate.EnsureActive();
        act.Should().NotThrow();
    }

    [Test]
    public void RevocableGate_Revoke_SetsIsActiveFalse()
    {
        var gate = new RevocableSessionActivityGate();
        gate.Revoke();
        gate.IsActive.Should().BeFalse();
    }

    [Test]
    public void RevocableGate_Revoke_CancelsRevocationToken()
    {
        var gate = new RevocableSessionActivityGate();
        gate.Revoke();
        gate.RevocationToken.IsCancellationRequested.Should().BeTrue();
    }

    [Test]
    public void RevocableGate_Revoke_EnsureActiveThrows()
    {
        var gate = new RevocableSessionActivityGate();
        gate.Revoke();
        var act = () => gate.EnsureActive();
        act.Should().ThrowExactly<InvalidOperationException>();
    }

    [Test]
    public void RevocableGate_Revoke_IsIdempotent()
    {
        var gate = new RevocableSessionActivityGate();
        gate.Revoke();
        gate.Revoke();
        gate.IsActive.Should().BeFalse();
        gate.RevocationToken.IsCancellationRequested.Should().BeTrue();
    }

    [Test]
    public async Task RevocableGate_Revoke_Twice_RunsCallbacksOnce()
    {
        var gate = new RevocableSessionActivityGate();
        var runs = 0;
        gate.RevocationToken.Register(() => Interlocked.Increment(ref runs));

        gate.Revoke();
        gate.Revoke();

        await gate.RevocationCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        runs.Should().Be(1);
    }

    // ── A blocked or throwing callback never reaches the revoking thread ────

    [Test]
    public async Task RevocableGate_Revoke_DoesNotWaitForABlockedCallback()
    {
        var gate = new RevocableSessionActivityGate();
        var release = new ManualResetEventSlim(false);

        try
        {
            // The safety bound only keeps a regression from hanging the run:
            // the callback normally ends when the test releases it below.
            gate.RevocationToken.Register(() => release.Wait(TimeSpan.FromSeconds(15)));

            var stopwatch = Stopwatch.StartNew();
            gate.Revoke();
            stopwatch.Stop();

            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
            gate.IsActive.Should().BeFalse();
            gate.RevocationToken.IsCancellationRequested.Should().BeTrue();
            gate.RevocationCompletion.IsCompleted.Should().BeFalse();
        }
        finally
        {
            release.Set();
        }

        await gate.RevocationCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        gate.RevocationCompletion.IsCompletedSuccessfully.Should().BeTrue();
        release.Dispose();
    }

    [Test]
    public async Task RevocableGate_ThrowingCallback_IsLoggedOnceCallbacksFinish()
    {
        var logger = Substitute.For<ILogger<RevocableSessionActivityGate>>();
        var gate = new RevocableSessionActivityGate(logger);
        gate.RevocationToken.Register(() => throw new InvalidOperationException("callback failed"));

        gate.Revoke();

        gate.IsActive.Should().BeFalse();
        await gate.RevocationCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        WarningMessages(logger).Should().Contain(message =>
            message.Contains("revocation callback failed"));
    }

    [Test]
    public async Task RevocableGate_Dispose_WhileCallbackBlocked_LeavesTheSourceToTheCallbacks()
    {
        var gate = new RevocableSessionActivityGate();
        var release = new ManualResetEventSlim(false);

        try
        {
            gate.RevocationToken.Register(() => release.Wait(TimeSpan.FromSeconds(15)));
            gate.Revoke();

            var stopwatch = Stopwatch.StartNew();
            gate.Dispose();
            stopwatch.Stop();

            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
        }
        finally
        {
            release.Set();
        }

        await gate.RevocationCompletion.WaitAsync(TimeSpan.FromSeconds(10));

        // The source is released once the callbacks it was still running finish.
        await WaitUntilDisposedAsync(gate);
        release.Dispose();
    }

    // ── Dispose/revoke contract: both orders end closed, cancelled, once ─────

    [Test]
    public async Task RevocableGate_DisposeThenRevoke_ClosesTheGateAndRunsCallbacksOnce()
    {
        var gate = new RevocableSessionActivityGate();
        var runs = 0;
        gate.RevocationToken.Register(() => Interlocked.Increment(ref runs));

        // Dispose implies revoke: the late Revoke must find the gate already
        // closed with its cancellation initiated, never skip the cancellation
        // because the source is gone.
        gate.Dispose();
        gate.Revoke();

        gate.IsActive.Should().BeFalse();
        var ensure = () => gate.EnsureActive();
        ensure.Should().ThrowExactly<InvalidOperationException>();

        await gate.RevocationCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        runs.Should().Be(1);
        await WaitUntilDisposedAsync(gate);
    }

    [Test]
    public async Task RevocableGate_RevokeThenDisposeRepeatedly_IdempotentWhileCallbacksRun()
    {
        var gate = new RevocableSessionActivityGate();
        var release = new ManualResetEventSlim(false);
        var runs = 0;

        try
        {
            gate.RevocationToken.Register(() =>
            {
                Interlocked.Increment(ref runs);
                release.Wait(TimeSpan.FromSeconds(15));
            });

            gate.Revoke();

            gate.Dispose();
            gate.Dispose();
            gate.Dispose();

            gate.IsActive.Should().BeFalse();
            gate.RevocationToken.IsCancellationRequested.Should().BeTrue();
            gate.RevocationCompletion.IsCompleted.Should().BeFalse();
        }
        finally
        {
            release.Set();
        }

        await gate.RevocationCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        runs.Should().Be(1);

        // The source is released once the callback finishes, and a Dispose
        // after that is still a no-op.
        await WaitUntilDisposedAsync(gate);
        gate.Dispose();
        release.Dispose();
    }

    private static IEnumerable<string> WarningMessages(ILogger logger)
        => logger.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(ILogger.Log)
                && Equals(call.GetArguments()[0], LogLevel.Warning))
            .Select(call => call.GetArguments()[2]?.ToString() ?? "");

    private static async Task WaitUntilDisposedAsync(RevocableSessionActivityGate gate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                _ = gate.RevocationToken;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new InvalidOperationException("Gate source was never disposed.");
    }
}
