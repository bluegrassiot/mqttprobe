using MQTTnet;
using MqttProbe.Core.Models.Chart;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Services.Chart;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Core.Services.Sparkplug;

namespace MqttProbe.IntegrationTests.Integration;

public sealed partial class SparkplugSequenceRecoveryTests
{
    [Test]
    public async Task ChartCollectionBeforeFirstChartsVisit_CollectsAliasOnlyUpdatesWithoutDatatype()
    {
        var groupId = $"chart-lifecycle-{Guid.NewGuid():N}";
        const string nodeId = "node";
        const string deviceId = "dev";
        var nodeTopic = $"spBv1.0/{groupId}/NDATA/{nodeId}";
        var deviceTopic = $"spBv1.0/{groupId}/DDATA/{nodeId}/{deviceId}";
        var connectionId = Guid.NewGuid();
        var nodeSeries = new ChartSeries { Id = Guid.NewGuid(), Topic = nodeTopic, JsonPath = "metrics.Flow Rate" };
        var deviceSeries = new ChartSeries { Id = Guid.NewGuid(), Topic = deviceTopic, JsonPath = "metrics.Running" };
        var chartSettings = Substitute.For<IChartSettings>();
        chartSettings.GetCharts(connectionId).Returns(
        [
            new ChartConfiguration { MaxPoints = 100, Series = [nodeSeries, deviceSeries] }
        ]);

        var topology = new SparkplugTopologyService();
        var pipeline = BuildRebuildPipeline();
        var sparkplugSettings = Substitute.For<ISparkplugSettings>();
        sparkplugSettings.Sparkplug.Returns(new SparkplugSettings { EnrichAliasNames = true });
        var client = new MqttManagedClient();
        var chartData = new ChartDataService(
            client, new JsonFieldExtractor(), new ChartFieldRegistry(), chartSettings, pipeline, sparkplugSettings,
            topology);
        using var publisher = new MqttClientFactory().CreateMqttClient();

        Task HandleTopologyAsync(MqttApplicationMessageReceivedEventArgs args)
        {
            var result = pipeline.ProcessInbound(args);
            return topology.ApplyTopologyEventsAsync(result.TopologyEvents);
        }

        try
        {
            await publisher.ConnectAsync(BuildTlsConnectOptions("chart-publisher").ClientOptions);
            client.ApplicationMessageReceivedAsync += HandleTopologyAsync;

            chartData.SetConnection(connectionId);
            await chartData.StartAsync();
            await client.SubscribeAsync(
            [
                Filter($"spBv1.0/{groupId}/NBIRTH/{nodeId}"),
                Filter(nodeTopic),
                Filter($"spBv1.0/{groupId}/DBIRTH/{nodeId}/{deviceId}"),
                Filter(deviceTopic)
            ]);
            await client.StartAsync(BuildTlsConnectOptions("chart-collector"));

            await publisher.PublishAsync(Message($"spBv1.0/{groupId}/NBIRTH/{nodeId}", 1,
                NamedDouble("Flow Rate", 42, 1.0)));
            await publisher.PublishAsync(Message($"spBv1.0/{groupId}/DBIRTH/{nodeId}/{deviceId}", 2,
                NamedBoolean("Running", 7, false)));

            await WaitForAsync(
                () => NodeMetrics(topology, groupId, nodeId) is { Count: 1 }
                      && DeviceMetrics(topology, groupId, nodeId, deviceId) is { Count: 1 },
                TimeSpan.FromSeconds(8),
                "the real broker births were not applied before data");

            await publisher.PublishAsync(Message(nodeTopic, 3, AliasDouble(42, 26.5)));
            await publisher.PublishAsync(Message(deviceTopic, 4, AliasBoolean(7, true)));

            await WaitForAsync(
                () => chartData.GetPoints(nodeSeries.Id).Count == 1
                      && chartData.GetPoints(deviceSeries.Id).Count == 1,
                TimeSpan.FromSeconds(8),
                "alias-only updates without a datatype did not reach the charts before the first Charts visit");

            chartData.GetPoints(nodeSeries.Id).Single().Value.Should().BeApproximately(26.5, 0.001);
            chartData.GetPoints(deviceSeries.Id).Single().Value.Should().Be(1.0);

            await chartData.StartAsync();
            chartData.SetConnection(connectionId);

            chartData.GetPoints(nodeSeries.Id).Should().ContainSingle(
                "opening Charts after connecting must preserve points already collected");
            chartData.GetPoints(deviceSeries.Id).Should().ContainSingle();

            var connectionB = Guid.NewGuid();
            var seriesB = new ChartSeries { Id = Guid.NewGuid(), Topic = nodeTopic, JsonPath = "metrics.Flow Rate" };
            chartSettings.GetCharts(connectionB).Returns(
            [
                new ChartConfiguration { MaxPoints = 100, Series = [seriesB] }
            ]);

            chartData.SetConnection(connectionB);
            chartData.GetPoints(nodeSeries.Id).Should().BeEmpty(
                "switching connections intentionally resets collected points");
            chartData.GetPoints(deviceSeries.Id).Should().BeEmpty();

            await publisher.PublishAsync(Message(nodeTopic, 5, AliasDouble(42, 31.5)));

            await WaitForAsync(
                () => chartData.GetPoints(seriesB.Id).Count == 1,
                TimeSpan.FromSeconds(8),
                "the changed connection's chart did not collect data");
            chartData.GetPoints(seriesB.Id).Single().Value.Should().BeApproximately(31.5, 0.001);
            chartData.GetPoints(nodeSeries.Id).Should().BeEmpty();
            chartData.GetPoints(deviceSeries.Id).Should().BeEmpty();
        }
        finally
        {
            client.ApplicationMessageReceivedAsync -= HandleTopologyAsync;
            chartData.Dispose();
            await client.StopAsync();
            if (publisher.IsConnected)
                await publisher.DisconnectAsync();
            await client.DisposeAsync();
        }
    }
}
