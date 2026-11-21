using System.Buffers;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Protocol;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Core.Services.Sparkplug;
using Org.Eclipse.Tahu.Protobuf;

namespace MqttProbe.IntegrationTests.Integration;

public sealed partial class SparkplugSequenceRecoveryTests
{
    [Test]
    public async Task MetricWrites_ArriveAsTypedNcmdAndDcmdOnTheBroker()
    {
        var groupId = $"write-{Guid.NewGuid():N}";
        const string nodeId = "node";
        const string deviceId = "dev";
        var ncmdTopic = $"spBv1.0/{groupId}/NCMD/{nodeId}";
        var dcmdTopic = $"spBv1.0/{groupId}/DCMD/{nodeId}/{deviceId}";

        var settings = Substitute.For<ISparkplugSettings>();
        settings.Sparkplug.Returns(new SparkplugSettings { AllowMetricWrites = true });
        var store = new SparkplugMetricMetadataStore();
        var topology = new SparkplugTopologyService(metricMetadataStore: store);
        var pipeline = BuildRebuildPipeline();
        var client = new MqttManagedClient();
        var commands = new SparkplugCommandService(
            client, Substitute.For<ILogger<SparkplugCommandService>>(), settings, topology);
        using var publisher = new MqttClientFactory().CreateMqttClient();
        using var observer = new MqttClientFactory().CreateMqttClient();
        using var lateSubscriber = new MqttClientFactory().CreateMqttClient();
        var received = new ConcurrentQueue<(string Topic, MqttQualityOfServiceLevel Qos, Payload Payload)>();
        var retained = new ConcurrentQueue<string>();

        Task HandleAsync(MqttApplicationMessageReceivedEventArgs args)
        {
            var result = pipeline.ProcessInbound(args, store);
            return topology.ApplyTopologyEventsAsync(result.TopologyEvents);
        }

        try
        {
            await publisher.ConnectAsync(BuildTlsConnectOptions("write-publisher", _broker).ClientOptions);
            await observer.ConnectAsync(BuildTlsConnectOptions("write-observer", _broker).ClientOptions);
            observer.ApplicationMessageReceivedAsync += args =>
            {
                var message = args.ApplicationMessage;
                received.Enqueue((
                    message.Topic,
                    message.QualityOfServiceLevel,
                    Payload.Parser.ParseFrom(message.Payload.ToArray())));
                return Task.CompletedTask;
            };
            await observer.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(Filter(ncmdTopic))
                .WithTopicFilter(Filter(dcmdTopic))
                .Build());

            client.ApplicationMessageReceivedAsync += HandleAsync;
            await client.SubscribeAsync(
            [
                Filter($"spBv1.0/{groupId}/NBIRTH/{nodeId}"),
                Filter($"spBv1.0/{groupId}/DBIRTH/{nodeId}/{deviceId}")
            ]);
            await client.StartAsync(BuildTlsConnectOptions("write-client", _broker));

            await publisher.PublishAsync(Message($"spBv1.0/{groupId}/NBIRTH/{nodeId}", 0,
                new Payload.Types.Metric { Name = "Setpoint", Datatype = 3, IntValue = 1 },
                new Payload.Types.Metric { Name = "Node Control/Reboot", Datatype = 11, BooleanValue = false }));
            await publisher.PublishAsync(Message($"spBv1.0/{groupId}/DBIRTH/{nodeId}/{deviceId}", 1,
                NamedDouble("Speed", 7, 2.0)));
            await WaitForAsync(
                () => NodeMetrics(topology, groupId, nodeId) is { Count: 2 }
                      && DeviceMetrics(topology, groupId, nodeId, deviceId) is { Count: 1 },
                TimeSpan.FromSeconds(8),
                "the real broker births were not applied to topology");

            (await commands.WriteNodeMetricAsync(groupId, nodeId, "Node Control/Reboot", "true"))
                .Should().Be(SparkplugCommandResult.NotAllowed);
            (await commands.WriteNodeMetricAsync(groupId, nodeId, "Setpoint", "-42"))
                .Should().Be(SparkplugCommandResult.Published);
            (await commands.WriteDeviceMetricAsync(groupId, nodeId, deviceId, "Speed", "12.345678901234"))
                .Should().Be(SparkplugCommandResult.Published);

            await WaitForAsync(
                () => received.Count >= 2, TimeSpan.FromSeconds(8), "the NCMD and DCMD did not reach the broker");

            received.Should().HaveCount(2, "the refused control-metric write must not reach the wire");
            var ncmd = received.Single(message => message.Topic == ncmdTopic);
            ncmd.Qos.Should().Be(MqttQualityOfServiceLevel.AtMostOnce);
            var ncmdPayload = ncmd.Payload;
            ncmdPayload.Timestamp.Should().BeGreaterThan(0UL);
            ncmdPayload.Seq.Should().Be(0UL);
            var nodeMetric = ncmdPayload.Metrics.Should().ContainSingle().Subject;
            nodeMetric.Name.Should().Be("Setpoint");
            nodeMetric.Datatype.Should().Be(3u);
            nodeMetric.ValueCase.Should().Be(Payload.Types.Metric.ValueOneofCase.IntValue);
            nodeMetric.IntValue.Should().Be(unchecked((uint)-42));

            var dcmd = received.Single(message => message.Topic == dcmdTopic);
            dcmd.Qos.Should().Be(MqttQualityOfServiceLevel.AtMostOnce);
            var deviceMetric = dcmd.Payload.Metrics.Should().ContainSingle().Subject;
            deviceMetric.Name.Should().Be("Speed");
            deviceMetric.Datatype.Should().Be(10u);
            deviceMetric.ValueCase.Should().Be(Payload.Types.Metric.ValueOneofCase.DoubleValue);
            deviceMetric.DoubleValue.Should().Be(12.345678901234d);

            await lateSubscriber.ConnectAsync(BuildTlsConnectOptions("write-late", _broker).ClientOptions);
            lateSubscriber.ApplicationMessageReceivedAsync += args =>
            {
                retained.Enqueue(args.ApplicationMessage.Topic);
                return Task.CompletedTask;
            };
            await lateSubscriber.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(Filter(ncmdTopic))
                .WithTopicFilter(Filter(dcmdTopic))
                .Build());
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            retained.Should().BeEmpty("commands must not be retained");
        }
        finally
        {
            client.ApplicationMessageReceivedAsync -= HandleAsync;
            await client.StopAsync();
            foreach (var raw in new[] { publisher, observer, lateSubscriber })
            {
                if (raw.IsConnected)
                    await raw.DisconnectAsync();
            }

            await client.DisposeAsync();
        }
    }
}
