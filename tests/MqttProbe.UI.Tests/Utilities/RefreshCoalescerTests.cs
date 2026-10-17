using MqttProbe.UI.Tests.TestHelpers;
using MqttProbe.UI.Utilities;

namespace MqttProbe.UI.Tests.Utilities;

[TestFixture]
public class RefreshCoalescerTests
{
    [Test]
    public async Task Request_InsideTheWindow_WaitsForTheWindowToElapse()
    {
        var refreshes = 0;
        using var coalescer = CreateCoalescer(windowMs: 300, () => Interlocked.Increment(ref refreshes));

        coalescer.Request();

        await Task.Delay(100);
        Volatile.Read(ref refreshes).Should().Be(0, "the refresh waits out the coalesce window");

        var refreshed = await WaitUntilAsync(() => Volatile.Read(ref refreshes) == 1, TimeSpan.FromSeconds(5));
        refreshed.Should().BeTrue("the coalesced refresh runs once the window elapses");
    }

    [Test]
    public async Task Request_AfterAnIdleWindow_RefreshesWithoutWaitingAnotherWindow()
    {
        var refreshes = 0;
        using var coalescer = CreateCoalescer(windowMs: 300, () => Interlocked.Increment(ref refreshes));

        coalescer.Request();
        await WaitUntilAsync(() => Volatile.Read(ref refreshes) == 1, TimeSpan.FromSeconds(5));

        await Task.Delay(400);

        coalescer.Request();

        var refreshed = await WaitUntilAsync(() => Volatile.Read(ref refreshes) == 2, TimeSpan.FromMilliseconds(200));
        refreshed.Should().BeTrue("an event landing after an idle window refreshes promptly");
    }

    [Test]
    public async Task BurstOfRequests_RefreshesAtTheWindowRate()
    {
        var refreshes = 0;
        var requests = 0;
        using var coalescer = CreateCoalescer(windowMs: 50, () => Interlocked.Increment(ref refreshes));

        var deadline = Environment.TickCount64 + 300;
        while (Environment.TickCount64 < deadline)
        {
            Interlocked.Increment(ref requests);
            coalescer.Request();
            await Task.Delay(5);
        }

        Volatile.Read(ref requests).Should().BeGreaterThan(15);
        Volatile.Read(ref refreshes).Should().BeInRange(1, 12,
            "a burst trickles at the window rate rather than one refresh per request");
    }

    [Test]
    public async Task Request_Burst_CoalescesIntoASingleRefresh()
    {
        var refreshes = 0;
        var blocked = new BlockedRenderDispatcher { Hold = true };
        using var coalescer = CreateCoalescer(windowMs: 600_000, () => Interlocked.Increment(ref refreshes));
        coalescer.DispatchOverrideForTest = blocked.Dispatch;

        for (var i = 0; i < 100; i++)
            coalescer.Request();
        coalescer.Tick();
        coalescer.Tick();

        blocked.PostedCount.Should().Be(1, "a tick cannot queue a second refresh while the first is outstanding");
        Volatile.Read(ref refreshes).Should().Be(0);

        await blocked.ReleaseAsync();

        Volatile.Read(ref refreshes).Should().Be(1);
        coalescer.ExecutedCount.Should().Be(1);
        coalescer.IsPending.Should().BeFalse();
    }

    [Test]
    public async Task DisposeAsync_ThenRequestAndTick_DoesNothing()
    {
        var refreshes = 0;
        var coalescer = CreateCoalescer(windowMs: 600_000, () => Interlocked.Increment(ref refreshes));

        coalescer.Request();
        await coalescer.DisposeAsync();

        coalescer.Request();
        coalescer.Tick();

        coalescer.QueuedCount.Should().Be(0, "a disposed coalescer queues nothing");
        Volatile.Read(ref refreshes).Should().Be(0);
    }

    private static RefreshCoalescer CreateCoalescer(int windowMs, Action onRefresh) =>
        new(work => work(), () =>
        {
            onRefresh();
            return Task.CompletedTask;
        }, windowMs);

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (!condition() && Environment.TickCount64 < deadline)
            await Task.Delay(5);
        return condition();
    }
}
