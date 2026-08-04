using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MqttProbe.Models.Configuration;
using MqttProbe.Models.Plugins;
using MqttProbe.Models.Sparkplug;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Emulation;
using MqttProbe.Services.Metrics;
using MqttProbe.Services.Mqtt;
using MqttProbe.Services.Platform;
using MqttProbe.Services.Plugins;
using MqttProbe.Services.Plugins.Loading;
using MqttProbe.Services.Plugins.Packaging;
using MqttProbe.Services.Plugins.Pipeline;
using MqttProbe.Services.Plugins.Registry;
using MqttProbe.Services.Security;
using MqttProbe.Services.Sparkplug;
using MqttProbe.Web.Services;
using Org.Eclipse.Tahu.Protobuf;

namespace MqttProbe.Tests.Services.Plugins;

// Proves the DI-composed object graph used by both Web and MAUI hosts builds correctly
// and routes messages end-to-end through the pipeline into both message storage and
// Sparkplug topology state.
[TestFixture]
public class PluginPipelineDiCompositionTests
{
    private ServiceProvider _serviceProvider = null!;
    private IMqttManagedClient _mockClient = null!;
    private Func<MqttApplicationMessageReceivedEventArgs, Task>? _capturedHandler;

    [SetUp]
    public void SetUp()
    {
        var services = new ServiceCollection();

        // --- Mocks, same pattern as MessageStoreManagerMessageHandlerTests ---
        _mockClient = Substitute.For<IMqttManagedClient>();
        _capturedHandler = null;
        _mockClient
            .When(x => x.ApplicationMessageReceivedAsync +=
                Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>())
            .Do(x => _capturedHandler =
                x.Arg<Func<MqttApplicationMessageReceivedEventArgs, Task>>());

        var config = new AppConfiguration();
        var mockPerformance = Substitute.For<IPerformanceSettings>();
        mockPerformance.Performance.Returns(config.Performance);
        var mockUi = Substitute.For<IUiSettings>();
        mockUi.Ui.Returns(config.Ui);

        services.AddLogging();
        services.AddSingleton(Options.Create(new PluginConfig()));
        services.AddSingleton(Substitute.For<IAppInfoService>());

        // The real shared registrations, not a copy of them. An earlier version of this test
        // re-declared the host wiring by hand and drifted out of sync with it, so it kept
        // passing while a host was genuinely broken.
        services.AddMqttProbeCore(HostSessionModel.SingleSession);
        services.AddMqttProbePlugins();
        services.AddMqttProbeSparkplugTopology(HostSessionModel.SingleSession);

        // Host-specific pieces the shared extensions deliberately leave to each host.
        services.AddSingleton<IPluginPackagePicker, WebPluginPackagePicker>();
        services.AddSingleton<IPluginInputCapability, WebPluginInputCapability>();

        // Substitutes registered last so they win over the real implementations above:
        // this fixture drives the pipeline through a captured message handler rather than a
        // live broker, and asserts against an in-memory config.
        services.AddSingleton(_mockClient);
        services.AddSingleton(mockPerformance);
        services.AddSingleton(mockUi);
        services.AddSingleton(Substitute.For<IConnectionSettings>());
        services.AddSingleton(Substitute.For<IEmulatorSettings>());
        services.AddSingleton(Substitute.For<IUxMetricsService>());

        _serviceProvider = services.BuildServiceProvider(validateScopes: true);
    }

    [TearDown]
    public void TearDown()
    {
        _serviceProvider.Dispose();
        _mockClient.Dispose();
    }

    private static MqttApplicationMessageReceivedEventArgs MakeArgs(string topic, string payload)
    {
        var appMsg = new MqttApplicationMessageBuilder()
            .WithTopic(topic).WithPayload(payload).Build();
        var packet = new MQTTnet.Packets.MqttPublishPacket { Topic = topic };
        return new MqttApplicationMessageReceivedEventArgs("test-client", appMsg, packet, null);
    }

    private static MqttApplicationMessageReceivedEventArgs MakeArgs(string topic, byte[] payload)
    {
        var appMsg = new MqttApplicationMessageBuilder()
            .WithTopic(topic).WithPayload(payload).Build();
        var packet = new MQTTnet.Packets.MqttPublishPacket { Topic = topic };
        return new MqttApplicationMessageReceivedEventArgs("test-client", appMsg, packet, null);
    }

    private static byte[] SpbPayload(params (string Name, ulong Alias, uint Datatype, double DoubleValue)[] metrics)
    {
        var p = new Payload { Timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
        foreach (var (name, alias, datatype, doubleValue) in metrics)
        {
            var m = new Payload.Types.Metric { Datatype = datatype, DoubleValue = doubleValue };
            if (!string.IsNullOrEmpty(name))
                m.Name = name;
            if (alias != 0)
                m.Alias = alias;
            p.Metrics.Add(m);
        }
        return p.ToByteArray();
    }

    [Test]
    public void ResolveFromDi_BuildsFullObjectGraph()
    {
        var manager = _serviceProvider.GetRequiredService<IMessageStoreManager>();

        manager.Should().NotBeNull();
        manager.Should().BeOfType<MessageStoreManager>();

        var pipeline = _serviceProvider.GetRequiredService<PayloadPipeline>();
        pipeline.Should().NotBeNull();

        var topology = _serviceProvider.GetRequiredService<ISparkplugTopologyService>();
        topology.Should().NotBeNull();
        topology.Should().BeOfType<SparkplugTopologyService>();
    }

    [Test]
    public void CoreAndSettingsTogether_ResolveEveryConsumerOfASettingsFacet()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<ICertificateAssetStore>());
        services.AddSingleton(Substitute.For<ICertificateEnvelopeKeyStore>());
        // PayloadPipeline is sealed, so it can't be substituted: EmulationService and
        // MessageStoreManager both require a real one, which only AddMqttProbePlugins provides.
        services.AddSingleton(Options.Create(new PluginConfig()));
        services.AddMqttProbeCore(HostSessionModel.SingleSession);
        services.AddMqttProbePlugins();
        services.AddMqttProbeSettings(Path.Combine(Path.GetTempPath(), $"c_{Guid.NewGuid()}.json"));
        using var provider = services.BuildServiceProvider();

        // These are the services that consume a settings facet. If AddMqttProbeCore is ever
        // called without AddMqttProbeSettings, this is where it fails — at container build,
        // not at first facet resolution deep in a running host.
        provider.GetRequiredService<IEmulationService>().Should().NotBeNull();
        provider.GetRequiredService<IMessageStoreManager>().Should().NotBeNull();
    }

    [Test]
    public void AddMqttProbeSettings_SharesOneInstanceAcrossEachFacetsInterfaces()
    {
        var services = new ServiceCollection();
        services.AddMqttProbeSettings(Path.Combine(Path.GetTempPath(), $"c_{Guid.NewGuid()}.json"));
        using var provider = services.BuildServiceProvider();

        var ui = provider.GetRequiredService<IUiSettings>();
        provider.GetRequiredService<IPerformanceSettings>().Should().BeSameAs(ui);
        provider.GetRequiredService<IAuthSettings>().Should().BeSameAs(ui,
            "PreferenceSettings owns two change events; a second instance drops subscribers");
    }

    [Test]
    public void ResolveFromDi_BuildsPluginPackagingServices()
    {
        _serviceProvider.GetRequiredService<PluginInventoryService>().Should().NotBeNull();
        _serviceProvider.GetRequiredService<PluginPackageInstaller>().Should().NotBeNull();
        _serviceProvider.GetRequiredService<PluginReloadService>().Should().NotBeNull();
    }

    [Test]
    public void ResolveFromDi_BuildsPluginPickerAndInputCapability()
    {
        _serviceProvider.GetRequiredService<IPluginPackagePicker>().Should().NotBeNull();

        var inputCapability = _serviceProvider.GetRequiredService<IPluginInputCapability>();
        inputCapability.Should().NotBeNull();
        inputCapability.UsesInputFileComponent.Should().BeTrue();
    }

    [Test]
    public void ResolveFromDi_PluginAssemblyCache_IsASingletonSharedByRegistryAndReloadService()
    {
        // PluginRegistry's factory and PluginReloadService both resolve PluginAssemblyCache
        // from sp rather than constructing their own; a singleton lifetime is what makes
        // Reload() reuse already-loaded assemblies instead of leaking a second
        // AssemblyLoadContext for the same DLLs. Force both through DI to prove it.
        _ = _serviceProvider.GetRequiredService<PluginRegistry>();
        _ = _serviceProvider.GetRequiredService<PluginReloadService>();

        var first = _serviceProvider.GetRequiredService<PluginAssemblyCache>();
        var second = _serviceProvider.GetRequiredService<PluginAssemblyCache>();

        first.Should().BeSameAs(second);
    }

    [Test]
    public async Task JsonMessage_RoutedThroughPipeline_StoredInMessageStore()
    {
        var manager = _serviceProvider.GetRequiredService<IMessageStoreManager>();
        await manager.Start();

        _capturedHandler.Should().NotBeNull();

        await _capturedHandler!(MakeArgs("data", """{"temp":21.5}"""));

        manager.MessageStores.Should().ContainKey("data");
        manager.MessageStores["data"].Messages
            .Should().Contain(m => m.Payload != null && m.Payload.Contains("temp"));
    }

    [Test]
    public async Task SparkplugNBirth_RoutedThroughPipeline_UpdatesTopologyAndStoresMessage()
    {
        var manager = _serviceProvider.GetRequiredService<IMessageStoreManager>();
        var topology = _serviceProvider.GetRequiredService<ISparkplugTopologyService>();
        await manager.Start();

        _capturedHandler.Should().NotBeNull();

        var payload = SpbPayload(("Temperature", 0, 10, 23.5));
        await _capturedHandler!(MakeArgs("spBv1.0/factory/NBIRTH/edge-01", payload));

        // Message stored
        manager.MessageStores.Should().ContainKey("spBv1.0");

        // Topology updated
        topology.Groups.Should().ContainKey("factory");
        topology.Groups["factory"].Nodes.Should().ContainKey("edge-01");
        topology.Groups["factory"].Nodes["edge-01"].Status
            .Should().Be(Models.Sparkplug.SpbNodeStatus.Online);
    }
}
