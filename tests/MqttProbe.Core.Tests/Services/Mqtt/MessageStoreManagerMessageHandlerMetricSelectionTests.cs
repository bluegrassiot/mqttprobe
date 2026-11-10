using Google.Protobuf;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Metrics;
using MqttProbe.Core.Services.Sparkplug;
using Org.Eclipse.Tahu.Protobuf;

namespace MqttProbe.Core.Tests.Services.Mqtt;

public partial class MessageStoreManagerMessageHandlerTests
{
    [Test]
    public async Task MetricSelection_UsesEachMessagesCapturedNamesAcrossAliasReassignment()
    {
        const string dataTopic = "spBv1.0/g/NDATA/n1";
        var sparkplug = Substitute.For<ISparkplugSettings>();
        sparkplug.Sparkplug.Returns(new SparkplugSettings { EnrichAliasNames = true });
        var topology = new SparkplugTopologyService(sparkplugSettings: sparkplug);
        var built = BuildManagerWithTopology(Substitute.For<IUxMetricsService>(), topology);
        using var manager = built.Manager;

        var received = new List<MqttMessage>();
        manager.MessageReceived += message =>
        {
            received.Add(message);
            return Task.CompletedTask;
        };

        await built.Fire(MakeBinaryArgs("spBv1.0/g/NBIRTH/n1", BirthPayload("A", 1u)));
        await built.Fire(MakeBinaryArgs(dataTopic, AliasDataPayload(2u)));
        await built.Fire(MakeBinaryArgs("spBv1.0/g/NBIRTH/n1", BirthPayload("B", 3u)));
        await built.Fire(MakeBinaryArgs(dataTopic, AliasDataPayload(4u)));

        received.Where(message => message.Topic == dataTopic)
            .Select(message => string.Join(",", message.SparkplugMetricNames))
            .Should().Equal("A", "B");

        manager.SelectTopics(TopicSelection.ForSparkplugMetric(NodeScope(), "A"));
        var selectedA = await manager.GetSelectedMessagesAsync(manager.GetSelectedTopicState().Token, 100);
        selectedA!.Messages.Where(message => message.Topic == dataTopic)
            .SelectMany(message => message.SparkplugMetricNames)
            .Should().Equal("A");

        manager.SelectTopics(TopicSelection.ForSparkplugMetric(NodeScope(), "B"));
        var selectedB = await manager.GetSelectedMessagesAsync(manager.GetSelectedTopicState().Token, 100);
        selectedB!.Messages.Where(message => message.Topic == dataTopic)
            .SelectMany(message => message.SparkplugMetricNames)
            .Should().Equal("B");
    }

    private static SparkplugSelectionScope NodeScope() =>
        new(SparkplugSelectionKind.Node, "spBv1.0/g", "n1");

    private static byte[] BirthPayload(string metricName, uint sequence)
    {
        var payload = new Payload { Timestamp = sequence, Seq = sequence };
        payload.Metrics.Add(new Payload.Types.Metric
        {
            Name = metricName,
            Alias = 7,
            Datatype = 3,
            IntValue = sequence
        });
        return payload.ToByteArray();
    }

    private static byte[] AliasDataPayload(uint sequence)
    {
        var payload = new Payload { Timestamp = sequence, Seq = sequence };
        payload.Metrics.Add(new Payload.Types.Metric
        {
            Alias = 7,
            Datatype = 10,
            DoubleValue = sequence
        });
        return payload.ToByteArray();
    }
}
