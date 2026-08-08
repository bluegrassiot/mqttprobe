using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Services.Chart;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Emulation;
using MqttProbe.Services.Metrics;
using MqttProbe.Services.Mqtt;
using MqttProbe.Services.Platform;
using MqttProbe.Services.Plugins.Packaging;
using MqttProbe.Services.Plugins.Pipeline;
using MqttProbe.Services.Plugins.Registry;
using MqttProbe.Services.Security;
using MqttProbe.Services.Sparkplug;

namespace MqttProbe.Shared.Tests.Services.Platform;

// Boots the real MqttProbe.Web host rather than a hand-rebuilt service collection. A service
// collection assembled inside a test is a copy of the host wiring, and a copy drifts: four
// Web-only registrations (IAppInfoService, IClipboardService, IUpdateService, IUserAuthService)
// were once dropped from Program.cs while every unit test still passed, because no test ever
// built the container Program.cs actually builds. The failure only appeared on launch.
//
// Program.cs runs builder.Build(), which validates the whole descriptor set, so simply starting
// the host is the assertion. The resolutions below cover the services a container validation
// cannot reach on its own -- those behind factory lambdas.
[TestFixture]
public class WebHostCompositionTests
{
    private WebApplicationFactory<Program> _factory = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp() => _factory = new WebApplicationFactory<Program>();

    [OneTimeTearDown]
    public void OneTimeTearDown() => _factory.Dispose();

    [Test]
    public void WebHost_BuildsItsServiceProvider()
    {
        // Build() throws AggregateException if any descriptor cannot be constructed, which is
        // exactly how the dropped registrations surfaced at runtime.
        var act = () => _factory.Services.GetRequiredService<ISettingsLoader>();
        act.Should().NotThrow();
    }

    // The four that went missing. Named individually so a failure says which one.
    [TestCase(typeof(IAppInfoService))]
    [TestCase(typeof(IUpdateService))]
    [TestCase(typeof(IUserAuthService))]
    [TestCase(typeof(IClipboardService))]
    public void WebHost_ResolvesHostSpecificService(Type serviceType)
    {
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetService(serviceType)
            .Should().NotBeNull($"{serviceType.Name} is registered only by the Web host");
    }

    // Services registered through factory lambdas: ValidateOnBuild cannot prove these resolve,
    // because it cannot see inside the lambda.
    [TestCase(typeof(ISecretStorage))]
    [TestCase(typeof(ICertificateAssetStore))]
    [TestCase(typeof(ICertificateEnvelopeKeyStore))]
    [TestCase(typeof(PluginRegistry))]
    [TestCase(typeof(PayloadPipeline))]
    [TestCase(typeof(PluginPackageInstaller))]
    [TestCase(typeof(IEmulationService))]
    [TestCase(typeof(IMqttManagedClient))]
    [TestCase(typeof(IMessageStoreManager))]
    [TestCase(typeof(ISparkplugTopologyService))]
    [TestCase(typeof(ISparkplugCommandService))]
    [TestCase(typeof(IUxMetricsService))]
    [TestCase(typeof(IChartDataService))]
    public void WebHost_ResolvesFactoryRegisteredService(Type serviceType)
    {
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetService(serviceType).Should().NotBeNull();
    }
}
