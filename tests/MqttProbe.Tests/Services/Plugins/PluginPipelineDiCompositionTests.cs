using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;
using MqttProbe.Models.Configuration;
using MqttProbe.Models.Plugins;
using MqttProbe.Models.Sparkplug;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Metrics;
using MqttProbe.Services.Mqtt;
using MqttProbe.Services.Plugins;
using MqttProbe.Services.Plugins.Pipeline;
using MqttProbe.Services.Plugins.Registry;
using MqttProbe.Services.Sparkplug;
using Org.Eclipse.Tahu.Protobuf;

namespace MqttProbe.Tests.Services.Plugins;

/// <summary>
/// Proves the DI-composed object graph used by both Web and MAUI hosts builds correctly
/// and routes messages end-to-end through the pipeline into both message storage and
/// Sparkplug topology state.
/// </summary>
[TestFixture]
public class PluginPipelineDiCompositionTests
{
    private ServiceProvider _serviceProvider = null!;
    private IManagedMqttClient _mockClient = null!;
    private Func<MqttApplicationMessageReceivedEventArgs, Task>? _capturedHandler;

    [SetUp]
    public void SetUp()
    {
        var services = new ServiceCollection();

        // --- Mocks, same pattern as MessageStoreManagerMessageHandlerTests ---
        _mockClient = Substitute.For<IManagedMqttClient>();
        _capturedHandler = null;
        _mockClient
            .When(x => x.ApplicationMessageReceivedAsync +=
                Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>())
            .Do(x => _capturedHandler =
                x.Arg<Func<MqttApplicationMessageReceivedEventArgs, Task>>());

        var mockSettings = Substitute.For<ISettingsStore>();
        mockSettings.Config.Returns(new AppConfiguration());

        // --- DI registrations mirroring Program.cs / MauiProgram.cs ---
        services.AddSingleton(_mockClient);
        services.AddSingleton(mockSettings);
        services.AddSingleton(Substitute.For<IUxMetricsService>());

        services.AddLogging();

        services.AddSingleton(Options.Create(new PluginConfig()));

        services.AddSingleton<PluginRegistry>(sp =>
        {
            var config = sp.GetRequiredService<IOptions<PluginConfig>>().Value;
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            return MqttProbePluginStartup.BuildPluginRegistry(config, loggerFactory);
        });

        services.AddSingleton<PayloadPipeline>();

        services.AddSingleton<ISparkplugTopologyService>(sp =>
            new SparkplugTopologyService(
                sp.GetRequiredService<IManagedMqttClient>(),
                sp.GetRequiredService<ILogger<SparkplugTopologyService>>(),
                autoSubscribeToClient: false));

        services.AddSingleton<IMessageStoreManager, MessageStoreManager>();

        _serviceProvider = services.BuildServiceProvider();
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
