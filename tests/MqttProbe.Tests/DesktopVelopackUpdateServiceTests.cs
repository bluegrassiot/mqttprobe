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
}
