using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MQTTnet;
using MqttProbe.Core;
using MqttProbe.Core.Models.Chart;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Plugins;
using MqttProbe.Core.Services.Chart;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Metrics;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Core.Services.Platform;
using MqttProbe.Core.Services.Plugins.BuiltIn;
using MqttProbe.Core.Services.Plugins.Packaging;
using MqttProbe.Core.Services.Plugins.Pipeline;
using MqttProbe.Core.Services.Sparkplug;
using MqttProbe.PluginContracts;
using Org.Eclipse.Tahu.Protobuf;

namespace MqttProbe.UI.Tests.Services.Plugins;

[TestFixture]
public class SparkplugSessionMetadataIsolationTests
{
    private const string DeviceBirthTopic = "spBv1.0/g/DBIRTH/n/d";
    private const string DeviceDataTopic = "spBv1.0/g/DDATA/n/d";

    private readonly List<IServiceScope> _scopes = [];
    private ServiceProvider _provider = null!;
    private Guid _seriesId;

    [SetUp]
    public void SetUp()
    {
        _seriesId = Guid.NewGuid();
        var config = new AppConfiguration();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new PluginConfig()));
        services.AddSingleton(Substitute.For<IAppInfoService>());
        services.AddSingleton(Substitute.For<IUserNotifier>());

        services.AddMqttProbeCore(HostSessionModel.PerCircuit);
        services.AddMqttProbePlugins();
        services.AddMqttProbeSparkplugTopology(HostSessionModel.PerCircuit);
        services.AddMqttProbeChartData();

        var performance = Substitute.For<IPerformanceSettings>();
        performance.Performance.Returns(config.Performance);
        var sparkplug = Substitute.For<ISparkplugSettings>();
        sparkplug.Sparkplug.Returns(new SparkplugSettings { EnrichAliasNames = true });
        var ui = Substitute.For<IUiSettings>();
        ui.Ui.Returns(config.Ui);
        var chartSettings = Substitute.For<IChartSettings>();
        chartSettings.GetCharts(Arg.Any<Guid>()).Returns(
        [
            new ChartConfiguration
            {
                MaxPoints = 100,
                Series =
                [
                    new ChartSeries
                    {
                        Id = _seriesId,
                        Topic = DeviceDataTopic,
                        JsonPath = "metrics.Delta"
                    }
                ]
            }
        ]);

        services.AddScoped<IMqttManagedClient>(_ => Substitute.For<IMqttManagedClient>());
        services.AddSingleton(performance);
        services.AddSingleton(ui);
        services.AddSingleton(sparkplug);
        services.AddSingleton(chartSettings);
        services.AddSingleton(Substitute.For<IConnectionSettings>());
        services.AddSingleton(Substitute.For<IEmulatorSettings>());
        services.AddSingleton(Substitute.For<IUxMetricsService>());

        _provider = services.BuildServiceProvider(validateScopes: true);
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var scope in _scopes)
            scope.Dispose();

        _scopes.Clear();
        _provider.Dispose();
    }

    [Test]
    public async Task TwoCircuitScopes_SameGroupNodeDevice_KeepSignedAndUnsignedMetadataIndependent()
    {
        var unsigned = CreateCircuit();
        var signed = CreateCircuit();
        await unsigned.StartAsync();
        await signed.StartAsync();

        await unsigned.Fire(DeviceBirthTopic, BirthPayload(datatype: 5));
        await signed.Fire(DeviceBirthTopic, BirthPayload(datatype: 1));
        await unsigned.Fire(DeviceDataTopic, DataPayload(206));
        await signed.Fire(DeviceDataTopic, DataPayload(206));

        AssertMetric(unsigned.Topology, "uint8", "206");
        AssertMetric(signed.Topology, "int8", "-50");
        unsigned.Metadata.Should().NotBeSameAs(signed.Metadata);
    }

    [TestCase("clear")]
    [TestCase("remove-node")]
    public async Task ResettingOneCircuit_LeavesAnotherCircuitsAliasTypeIntact(string reset)
    {
        var resetCircuit = CreateCircuit();
        var keptCircuit = CreateCircuit();
        await resetCircuit.StartAsync();
        await keptCircuit.StartAsync();

        await resetCircuit.Fire(DeviceBirthTopic, BirthPayload(datatype: 5));
        await keptCircuit.Fire(DeviceBirthTopic, BirthPayload(datatype: 1));

        if (reset == "clear")
            resetCircuit.Topology.ClearAll();
        else
            resetCircuit.Topology.RemoveNode("g", "n").Should().BeTrue();

        await resetCircuit.Fire(DeviceDataTopic, DataPayload(206));
        await keptCircuit.Fire(DeviceDataTopic, DataPayload(206));

        resetCircuit.Topology.Groups["g"].Nodes["n"].Devices["d"].Metrics.Should().BeEmpty();
        AssertMetric(keptCircuit.Topology, "int8", "-50");
    }

    [Test]
    public async Task RegistryReload_KeepsSessionMetadataAndBuiltInSelection()
    {
        var circuit = CreateCircuit();
        await circuit.StartAsync();
        await circuit.Fire(DeviceBirthTopic, BirthPayload(datatype: 1));

        var previousRegistry = circuit.Pipeline.Registry;
        var reload = circuit.Scope.ServiceProvider.GetRequiredService<PluginReloadService>();

        reload.Reload().Succeeded.Should().BeTrue();

        circuit.Pipeline.Registry.Should().NotBeSameAs(previousRegistry);
        circuit.Pipeline.Registry.FindTopologyExtractor("sparkplug-b").Should().NotBeNull();
        circuit.Pipeline.Registry.FindDecoder("sparkplug-b").Should().NotBeNull();

        await circuit.Fire(DeviceDataTopic, DataPayload(206));

        AssertMetric(circuit.Topology, "int8", "-50");
    }

    [Test]
    public async Task ChartService_ExtractingBirthWithoutApplying_DoesNotPopulateSessionMetadata()
    {
        var circuit = CreateCircuit();
        var chart = circuit.Scope.ServiceProvider.GetRequiredService<IChartDataService>();
        await chart.StartAsync();

        await circuit.Fire(DeviceBirthTopic, BirthPayload(datatype: 1));
        circuit.Metadata.ResolveDatatype("g", "n", "d", 42, "Delta").Should().BeNull();

        var birthEvents = new SparkplugTopologyExtractor().Extract(Envelope(DeviceBirthTopic, BirthPayload(1)));
        await circuit.Topology.ApplyTopologyEventsAsync(birthEvents);

        await circuit.Fire(DeviceDataTopic, DataPayload(206));

        chart.GetPoints(_seriesId).Should().ContainSingle().Which.Value.Should().Be(-50.0);
    }

    private Circuit CreateCircuit()
    {
        var scope = _provider.CreateScope();
        _scopes.Add(scope);
        var services = scope.ServiceProvider;

        var client = services.GetRequiredService<IMqttManagedClient>();
        var circuit = new Circuit(
            scope,
            services.GetRequiredService<IMessageStoreManager>(),
            services.GetRequiredService<ISparkplugTopologyService>(),
            services.GetRequiredService<SparkplugMetricMetadataStore>(),
            services.GetRequiredService<PayloadPipeline>());

        client
            .When(x => x.ApplicationMessageReceivedAsync +=
                Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>())
            .Do(x => circuit.Handler =
                x.Arg<Func<MqttApplicationMessageReceivedEventArgs, Task>>());

        return circuit;
    }

    private static void AssertMetric(ISparkplugTopologyService topology, string dataType, string value)
    {
        var metric = topology.Groups["g"].Nodes["n"].Devices["d"].Metrics
            .Should().ContainSingle(snapshot => snapshot.Name == "Delta").Subject;
        metric.DataType.Should().Be(dataType);
        metric.Value.Should().Be(value);
    }

    private static byte[] BirthPayload(uint datatype) =>
        Payload(new Payload.Types.Metric { Name = "Delta", Alias = 42, Datatype = datatype, IntValue = 200 });

    private static byte[] DataPayload(uint rawValue) =>
        Payload(new Payload.Types.Metric { Alias = 42, IntValue = rawValue });

    private static byte[] Payload(params Payload.Types.Metric[] metrics)
    {
        var payload = new Org.Eclipse.Tahu.Protobuf.Payload
        {
            Timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        payload.Metrics.AddRange(metrics);
        return payload.ToByteArray();
    }

    private static DecodedPayloadEnvelope Envelope(string topic, byte[] payload)
    {
        var parsed = Org.Eclipse.Tahu.Protobuf.Payload.Parser.ParseFrom(payload);
        return DecodedPayloadEnvelope.CreateSuccess(
            "sparkplug-b", topic, payload, parsed.ToString(), typedPayload: parsed);
    }

    private static MqttApplicationMessageReceivedEventArgs MakeArgs(string topic, byte[] payload)
    {
        var appMessage = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .Build();
        var packet = new MQTTnet.Packets.MqttPublishPacket { Topic = topic };
        return new MqttApplicationMessageReceivedEventArgs("test-client", appMessage, packet, null);
    }

    private sealed class Circuit(
        IServiceScope scope,
        IMessageStoreManager manager,
        ISparkplugTopologyService topology,
        SparkplugMetricMetadataStore metadata,
        PayloadPipeline pipeline)
    {
        public IServiceScope Scope { get; } = scope;
        public ISparkplugTopologyService Topology { get; } = topology;
        public SparkplugMetricMetadataStore Metadata { get; } = metadata;
        public PayloadPipeline Pipeline { get; } = pipeline;

        public Func<MqttApplicationMessageReceivedEventArgs, Task>? Handler { get; set; }

        public Task StartAsync() => manager.Start();

        public Task Fire(string topic, byte[] payload) => Handler!(MakeArgs(topic, payload));
    }
}
