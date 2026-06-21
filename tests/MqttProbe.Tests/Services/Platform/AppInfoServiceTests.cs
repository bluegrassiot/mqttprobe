using MqttProbe.Services.Chart;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Mqtt;
using MqttProbe.Services.Platform;
using MqttProbe.Services.Security;
using MqttProbe.Services.Sparkplug;
using MqttProbe.Services.Telemetry;
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
    public void GetVersion_WhenProcessVersionLookupThrows_FallsBackToAssemblyVersion()
    {
        var service = new AppInfoService(
            () => throw new InvalidOperationException("process metadata unavailable"),
            () => "1.2.3+build");

        service.GetVersion().Should().Be("1.2.3");
    }

    [Test]
    public void GetVersion_WhenAllVersionLookupsThrow_ReturnsUnknown()
    {
        var service = new AppInfoService(
            () => throw new InvalidOperationException("process metadata unavailable"),
            () => throw new InvalidOperationException("assembly metadata unavailable"));

        service.GetVersion().Should().Be("unknown");
    }

    [Test]
    public void RequiresAuthentication_ReturnsTrue()
    {
        _service.RequiresAuthentication.Should().BeTrue();
    }
}
