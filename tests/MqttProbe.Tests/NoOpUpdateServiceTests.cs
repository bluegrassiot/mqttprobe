using MqttProbe.Services.Platform;

namespace MqttProbe.Tests;

[TestFixture]
public class NoOpUpdateServiceTests
{
    [Test]
    public void IsSupported_IsFalse()
        => new NoOpUpdateService().IsSupported.Should().BeFalse();

    [Test]
    public async Task CheckForUpdateAsync_ReturnsNull()
        => (await new NoOpUpdateService().CheckForUpdateAsync()).Should().BeNull();

    [Test]
    public void DownloadAndApplyAsync_Completes()
        => new NoOpUpdateService().DownloadAndApplyAsync().IsCompletedSuccessfully.Should().BeTrue();
}
