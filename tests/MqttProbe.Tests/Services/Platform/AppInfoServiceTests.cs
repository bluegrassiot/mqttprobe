using MqttProbe.Web.Services;

namespace MqttProbe.Shared.Tests.Services.Platform;

[TestFixture]
public class AppInfoServiceTests
{
    private AppInfoService _service = null!;

    [SetUp]
    public void Setup() => _service = new AppInfoService();

    [Test]
    public void GetVersion_ReturnsNonNullNonEmptyString()
    {
        var version = _service.GetVersion();
        version.Should().NotBeNullOrEmpty();
    }

    [Test]
    public void GetVersion_DoesNotContainPlusSign()
    {
        var version = _service.GetVersion();
        version.Should().NotContain("+");
    }

    [Test]
    public void GetVersion_WhenVersionSourcesUnavailable_ReturnsUnknown()
    {
        var service = new AppInfoService(() => null, () => null);

        service.GetVersion().Should().Be("unknown");
    }

    [Test]
    public void GetVersion_WhenAssemblyVersionLookupThrows_FallsBackToProcessVersion()
    {
        var service = new AppInfoService(
            () => throw new InvalidOperationException("assembly metadata unavailable"),
            () => "1.2.3+build");

        service.GetVersion().Should().Be("1.2.3");
    }

    [Test]
    public void GetVersion_WhenBothProvidersReturnValues_PrefersAssemblyInformationalVersion()
    {
        var service = new AppInfoService(
            () => "1.2.3+build",
            () => "9.9.9");

        service.GetVersion().Should().Be("1.2.3");
    }

    [Test]
    public void GetVersion_WhenAllVersionLookupsThrow_ReturnsUnknown()
    {
        var service = new AppInfoService(
            () => throw new InvalidOperationException("assembly metadata unavailable"),
            () => throw new InvalidOperationException("process metadata unavailable"));

        service.GetVersion().Should().Be("unknown");
    }

    [Test]
    public void RequiresAuthentication_ReturnsTrue()
    {
        _service.RequiresAuthentication.Should().BeTrue();
    }
}
