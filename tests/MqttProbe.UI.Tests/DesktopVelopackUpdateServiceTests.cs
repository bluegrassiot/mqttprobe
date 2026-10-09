using Microsoft.Extensions.Logging.Abstractions;
using MqttProbe.Desktop.Services;

namespace MqttProbe.Tests;

[TestFixture]
public class DesktopVelopackUpdateServiceTests
{
    private static DesktopVelopackUpdateService Create()
        => new(NullLogger<DesktopVelopackUpdateService>.Instance);

    [Test]
    public void Constructor_DoesNotThrow_OutsideInstalledApp()
        => ((Func<DesktopVelopackUpdateService>)Create).Should().NotThrow();

    [Test]
    public void IsSupported_IsFalse_OutsideInstalledApp()
        => Create().IsSupported.Should().BeFalse();

    [Test]
    public async Task CheckForUpdateAsync_ReturnsNull_WhenUnsupported()
        => (await Create().CheckForUpdateAsync()).Should().BeNull();

    [Test]
    public void DownloadAndApplyAsync_Completes_WhenNothingPending()
        => Create().DownloadAndApplyAsync().IsCompletedSuccessfully.Should().BeTrue();

#if DEBUG
    [Test]
    public void Simulation_IsSupported()
        => CreateSimulation().IsSupported.Should().BeTrue();

    [Test]
    public async Task Simulation_CheckForUpdateAsync_ReturnsSimulatedVersion()
        => (await CreateSimulation().CheckForUpdateAsync()).Should().Be("999.0.0-simulation");

    [Test]
    public async Task Simulation_DownloadAndApplyAsync_WaitsUntilCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        var operation = CreateSimulation().DownloadAndApplyAsync(cancellation.Token);

        operation.IsCompleted.Should().BeFalse();
        cancellation.Cancel();
        await Assert.CatchAsync<OperationCanceledException>(async () => await operation);
    }

    private static MqttProbe.Desktop.Program.SimulatedUpdateService CreateSimulation()
        => new();
#endif
}
