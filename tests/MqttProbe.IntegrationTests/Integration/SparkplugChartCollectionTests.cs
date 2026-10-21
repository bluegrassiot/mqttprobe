using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Protocol;
using MqttProbe.Core.Models.Chart;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Services.Chart;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Metrics;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Core.Services.Plugins.BuiltIn;
using MqttProbe.Core.Services.Plugins.Pipeline;
using MqttProbe.Core.Services.Plugins.Registry;
using MqttProbe.Core.Services.Sparkplug;
using MqttProbe.TestInfrastructure.Fixtures;
using Org.Eclipse.Tahu.Protobuf;

namespace MqttProbe.IntegrationTests.Integration;

[TestFixture]
public class SparkplugChartCollectionTests
{
    private const string MetricName = "Tank Level";
    private const ulong MetricAlias = 42;

    private static MtlsBrokerFixture? _broker;

    [OneTimeSetUp]
    public async Task Setup() => _broker = await MtlsBrokerFixture.StartAsync();

    [OneTimeTearDown]
    public async Task Teardown()
    {
        if (_broker is not null)
            await _broker.DisposeAsync();
    }

    [Test]
    public async Task NodeDataUpdate_FromBroker_PopulatesChartSeries_ForAliasOnlyMetric()
    {
        var groupId = $"chart-{Guid.NewGuid():N}";
        const string nodeId = "node";
        var birthTopic = $"spBv1.0/{groupId}/NBIRTH/{nodeId}";
        var dataTopic = $"spBv1.0/{groupId}/NDATA/{nodeId}";

        var connectionId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var chartSettings = Substitute.For<IChartSettings>();
        chartSettings.GetCharts(connectionId).Returns(
        [
            new ChartConfiguration
            {
                MaxPoints = 100,
                Series = [new ChartSeries { Id = seriesId, Topic = dataTopic, JsonPath = $"metrics.{MetricName}" }]
            }
        ]);

        using var client = new MqttManagedClient();
        var sparkplug = Substitute.For<ISparkplugSettings>();
        sparkplug.Sparkplug.Returns(new SparkplugSettings { EnrichAliasNames = true });
        var topology = new SparkplugTopologyService(sparkplugSettings: sparkplug);
        var performance = Substitute.For<IPerformanceSettings>();
        performance.Performance.Returns(new PerformanceSettings
        {
            MaxStoredMessages = 100,
            MaxMessagesPerSecond = 50_000
        });
        var pipeline = BuildBuiltInPipeline();
        using var store = new MessageStoreManager(
            client,
            Substitute.For<ILogger<MessageStoreManager>>(),
            performance,
            Substitute.For<IUxMetricsService>(),
            pipeline,
            sparkplug,
            topology);
        using var chart = new ChartDataService(
            client,
            new JsonFieldExtractor(),
            new ChartFieldRegistry(),
            chartSettings,
            pipeline,
            sparkplug,
            topology);
        chart.SetConnection(connectionId);

        try
        {
            await store.Start();
            await chart.StartAsync();

            var filters = new[] { birthTopic, dataTopic }
                .Select(topic => new MqttTopicFilterBuilder().WithTopic(topic)
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build())
                .ToArray();
            await client.SubscribeAsync(filters);
            await client.StartAsync(BuildTlsConnectOptions("chart-client"));
            await client.SubscribeAsync(filters);

            using var publisher = new MqttClientFactory().CreateMqttClient();
            await publisher.ConnectAsync(BuildTlsConnectOptions("chart-publisher").ClientOptions);

            var birth = new Payload { Timestamp = 2, Seq = 1 };
            birth.Metrics.Add(new Payload.Types.Metric
            {
                Name = MetricName,
                Datatype = 10,
                DoubleValue = 1.0,
                Alias = MetricAlias
            });
            await PublishAsync(publisher, birthTopic, birth);

            await WaitForAsync(
                () => topology.Groups.TryGetValue(groupId, out var group) && group.Nodes.ContainsKey(nodeId),
                TimeSpan.FromSeconds(10),
                "the node birth never reached the topology service");

            var data = new Payload { Timestamp = 3, Seq = 2 };
            data.Metrics.Add(new Payload.Types.Metric { Alias = MetricAlias, Datatype = 10, DoubleValue = 24.5 });
            await PublishAsync(publisher, dataTopic, data);

            await WaitForAsync(
                () => chart.GetPoints(seriesId).Count > 0,
                TimeSpan.FromSeconds(10),
                "the node data update never reached the chart series");

            chart.GetPoints(seriesId).Should().ContainSingle()
                .Which.Value.Should().BeApproximately(24.5, 0.001);
        }
        finally
        {
            await store.Stop();
            await client.StopAsync();
        }
    }

    private static Task PublishAsync(IMqttClient publisher, string topic, Payload payload) =>
        publisher.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload.ToByteArray())
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build());

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout, string because)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !condition())
            await Task.Delay(50);

        condition().Should().BeTrue(because);
    }

    private static MqttManagedClientOptions BuildTlsConnectOptions(string clientIdSuffix)
    {
        var clientOpts = new MqttClientOptionsBuilder()
            .WithTcpServer("127.0.0.1", _broker!.Port)
            .WithClientId($"integ-{clientIdSuffix}-{Guid.NewGuid():N}")
            .WithCleanSession()
            .WithTlsOptions(o => o
                .WithSslProtocols(System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13) // DevSkim: ignore DS440020,DS112836,DS440001
                .WithClientCertificates(new X509Certificate2Collection(
                    X509CertificateLoader.LoadPkcs12(
                        _broker.ClientPfxBytes, _broker.ClientPfxPassword,
                        X509KeyStorageFlags.Exportable)))
                .WithCertificateValidationHandler(ctx =>
                {
                    using var chain = new X509Chain();
                    chain.ChainPolicy.ExtraStore.Add(_broker.CaCert);
                    chain.ChainPolicy.VerificationFlags =
                        X509VerificationFlags.AllowUnknownCertificateAuthority
                        | X509VerificationFlags.IgnoreCertificateAuthorityRevocationUnknown
                        | X509VerificationFlags.IgnoreEndRevocationUnknown
                        | X509VerificationFlags.IgnoreRootRevocationUnknown;
                    return chain.Build((X509Certificate2)ctx.Certificate);
                }))
            .Build();

        return new MqttManagedClientOptions
        {
            ClientOptions = clientOpts,
            AutoReconnectDelay = TimeSpan.FromSeconds(60)
        };
    }

    private static PayloadPipeline BuildBuiltInPipeline()
    {
        var builder = new PluginRegistryBuilder();
        BuiltInPluginRegistration.RegisterBuiltIns(builder);
        return new PayloadPipeline(builder.Build(), Substitute.For<ILogger<PayloadPipeline>>());
    }
}
