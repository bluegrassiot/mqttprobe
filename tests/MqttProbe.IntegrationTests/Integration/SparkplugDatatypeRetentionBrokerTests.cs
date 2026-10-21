using Google.Protobuf;
using MQTTnet;
using MQTTnet.Protocol;
using MqttProbe.Core.Models.Sparkplug;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Core.Services.Sparkplug;
using Org.Eclipse.Tahu.Protobuf;

namespace MqttProbe.IntegrationTests.Integration;

public sealed partial class SparkplugSequenceRecoveryTests
{
    [Test]
    public async Task BrokerBirthThenAliasOnlyDataWithoutDatatype_RetainsKnownTypesAndValues()
    {
        var groupId = $"datatype-{Guid.NewGuid():N}";
        const string nodeId = "node";
        const string deviceId = "dev";
        var topology = new SparkplugTopologyService();
        var pipeline = BuildRebuildPipeline();
        var client = new MqttManagedClient();
        using var publisher = new MqttClientFactory().CreateMqttClient();

        Task HandleAsync(MqttApplicationMessageReceivedEventArgs args)
        {
            var result = pipeline.ProcessInbound(args);
            return topology.ApplyTopologyEventsAsync(result.TopologyEvents);
        }

        try
        {
            await publisher.ConnectAsync(BuildTlsConnectOptions("datatype-publisher", _broker).ClientOptions);
            client.ApplicationMessageReceivedAsync += HandleAsync;
            await client.SubscribeAsync(
            [
                Filter($"spBv1.0/{groupId}/NBIRTH/{nodeId}"),
                Filter($"spBv1.0/{groupId}/NDATA/{nodeId}"),
                Filter($"spBv1.0/{groupId}/DBIRTH/{nodeId}/{deviceId}"),
                Filter($"spBv1.0/{groupId}/DDATA/{nodeId}/{deviceId}")
            ]);
            await client.StartAsync(BuildTlsConnectOptions("datatype-client", _broker));

            await publisher.PublishAsync(Message($"spBv1.0/{groupId}/NBIRTH/{nodeId}", 1,
                NamedDouble("Pressure", 42, 1.0),
                NamedBoolean("Ready", 43, true),
                NamedString("Label", 44, "42"),
                NamedLong("Timestamp", 45, 1714050000000)));
            await publisher.PublishAsync(Message($"spBv1.0/{groupId}/DBIRTH/{nodeId}/{deviceId}", 2,
                NamedDouble("Flow Rate", 7, 2.0)));

            await WaitForAsync(
                () => NodeMetrics(topology, groupId, nodeId) is { Count: 4 }
                      && DeviceMetrics(topology, groupId, nodeId, deviceId) is { Count: 1 },
                TimeSpan.FromSeconds(8),
                "the real broker births were not applied to topology");

            await publisher.PublishAsync(Message($"spBv1.0/{groupId}/NDATA/{nodeId}", 3,
                AliasDouble(42, 3.5),
                AliasBoolean(43, false),
                AliasString(44, "true"),
                AliasLong(45, 1714050001000)));
            await publisher.PublishAsync(Message($"spBv1.0/{groupId}/DDATA/{nodeId}/{deviceId}", 4,
                AliasDouble(7, 4.5)));

            await WaitForAsync(
                () => HasMetric(NodeMetrics(topology, groupId, nodeId), "Pressure", "double", "3.5000")
                      && HasMetric(DeviceMetrics(topology, groupId, nodeId, deviceId), "Flow Rate", "double", "4.5000"),
                TimeSpan.FromSeconds(8),
                "alias-only data without a datatype did not retain the birth type over the wire");

            var nodeMetrics = NodeMetrics(topology, groupId, nodeId)!;
            HasMetric(nodeMetrics, "Ready", "bool", "false").Should().BeTrue();
            HasMetric(nodeMetrics, "Label", "string", "true").Should().BeTrue();
            HasMetric(nodeMetrics, "Timestamp", "datetime", "1714050001000").Should().BeTrue();
        }
        finally
        {
            client.ApplicationMessageReceivedAsync -= HandleAsync;
            await client.StopAsync();
            if (publisher.IsConnected)
                await publisher.DisconnectAsync();
            await client.DisposeAsync();
        }
    }

    private static IReadOnlyList<SpbMetricSnapshot>? NodeMetrics(
        ISparkplugTopologyService topology, string groupId, string nodeId) =>
        topology.Groups.TryGetValue(groupId, out var group)
        && group.Nodes.TryGetValue(nodeId, out var node)
            ? node.Metrics
            : null;

    private static IReadOnlyList<SpbMetricSnapshot>? DeviceMetrics(
        ISparkplugTopologyService topology, string groupId, string nodeId, string deviceId) =>
        topology.Groups.TryGetValue(groupId, out var group)
        && group.Nodes.TryGetValue(nodeId, out var node)
        && node.Devices.TryGetValue(deviceId, out var device)
            ? device.Metrics
            : null;

    private static bool HasMetric(
        IEnumerable<SpbMetricSnapshot>? metrics, string name, string dataType, string value) =>
        metrics?.Any(metric => metric.Name == name && metric.DataType == dataType && metric.Value == value) == true;

    private static MqttApplicationMessage Message(string topic, ulong sequence, params Payload.Types.Metric[] metrics)
    {
        var payload = new Payload { Timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Seq = sequence };
        payload.Metrics.AddRange(metrics);
        return new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload.ToByteArray())
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();
    }

    private static Payload.Types.Metric NamedDouble(string name, ulong alias, double value) =>
        new() { Name = name, Alias = alias, Datatype = 10, DoubleValue = value };

    private static Payload.Types.Metric NamedBoolean(string name, ulong alias, bool value) =>
        new() { Name = name, Alias = alias, Datatype = 11, BooleanValue = value };

    private static Payload.Types.Metric NamedString(string name, ulong alias, string value) =>
        new() { Name = name, Alias = alias, Datatype = 12, StringValue = value };

    private static Payload.Types.Metric NamedLong(string name, ulong alias, ulong value) =>
        new() { Name = name, Alias = alias, Datatype = 14, LongValue = value };

    private static Payload.Types.Metric AliasDouble(ulong alias, double value) =>
        new() { Alias = alias, DoubleValue = value };

    private static Payload.Types.Metric AliasBoolean(ulong alias, bool value) =>
        new() { Alias = alias, BooleanValue = value };

    private static Payload.Types.Metric AliasString(ulong alias, string value) =>
        new() { Alias = alias, StringValue = value };

    private static Payload.Types.Metric AliasLong(ulong alias, ulong value) =>
        new() { Alias = alias, LongValue = value };
}
