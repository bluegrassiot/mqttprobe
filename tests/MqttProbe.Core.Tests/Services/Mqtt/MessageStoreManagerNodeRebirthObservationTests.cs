using Google.Protobuf;
using Microsoft.Extensions.Time.Testing;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Sparkplug;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Metrics;
using MqttProbe.Core.Services.Sparkplug;
using Org.Eclipse.Tahu.Protobuf;

namespace MqttProbe.Core.Tests.Services.Mqtt;

public partial class MessageStoreManagerMessageHandlerTests
{
    private const string NcmdGroupId = "ncmd-group";
    private const string NcmdNodeId = "node";

    private static byte[] NbirthPayload()
    {
        var payload = new Payload { Timestamp = 1, Seq = 1 };
        payload.Metrics.Add(new Payload.Types.Metric { Name = "bdSeq", Datatype = 5, IntValue = 1 });
        payload.Metrics.Add(new Payload.Types.Metric { Name = "Temp", Datatype = 10, DoubleValue = 21.5 });
        return payload.ToByteArray();
    }

    private static byte[] NcmdPayload(string metricName, bool value)
    {
        var payload = new Payload { Timestamp = 2 };
        payload.Metrics.Add(new Payload.Types.Metric
        {
            Name = metricName,
            Datatype = 11,
            BooleanValue = value
        });
        return payload.ToByteArray();
    }

    [Test]
    public async Task NodeRebirthObservation_KnownNodeThroughPipeline_UpdatesTimestampOnly()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        var sparkplug = Substitute.For<ISparkplugSettings>();
        sparkplug.Sparkplug.Returns(new SparkplugSettings { EnrichAliasNames = true });
        var topology = new SparkplugTopologyService(clock, sparkplugSettings: sparkplug);
        var commands = Substitute.For<ISparkplugCommandService>();
        var built = BuildManagerWithTopology(Substitute.For<IUxMetricsService>(), topology, commands);
        using var manager = built.Manager;

        await built.Fire(MakeBinaryArgs($"spBv1.0/{NcmdGroupId}/NBIRTH/{NcmdNodeId}", NbirthPayload()));

        var node = topology.Groups[NcmdGroupId].Nodes[NcmdNodeId];
        node.Status.Should().Be(SpbNodeStatus.Online);
        node.LastRebirthRequestAt.Should().BeNull();

        var sequence = node.Sequence;
        var changed = 0;
        topology.TopologyChanged += () => changed++;

        await built.Fire(MakeBinaryArgs(
            $"spBv1.0/{NcmdGroupId}/NCMD/{NcmdNodeId}",
            NcmdPayload(SparkplugControlMetrics.NodeRebirth, true)));

        node.LastRebirthRequestAt.Should().Be(clock.GetUtcNow().UtcDateTime);
        node.Status.Should().Be(SpbNodeStatus.Online);
        node.LastDataAt.Should().BeNull();
        node.Sequence.Should().Be(sequence);
        changed.Should().Be(1);
        commands.ReceivedCalls().Should().BeEmpty("an observed rebirth request is not a sequence recovery trigger");
    }

    [Test]
    public async Task NodeRebirthObservation_UnknownNodeThroughPipeline_CreatesNoTopology()
    {
        var topology = new SparkplugTopologyService();
        var built = BuildManagerWithTopology(Substitute.For<IUxMetricsService>(), topology);
        using var manager = built.Manager;

        await built.Fire(MakeBinaryArgs(
            $"spBv1.0/{NcmdGroupId}/NCMD/ghost",
            NcmdPayload(SparkplugControlMetrics.NodeRebirth, true)));

        topology.Groups.Should().BeEmpty();
    }

    [Test]
    public async Task NodeRebirthObservation_NonRebirthNcmd_LeavesTimestampUnset()
    {
        var topology = new SparkplugTopologyService();
        var built = BuildManagerWithTopology(Substitute.For<IUxMetricsService>(), topology);
        using var manager = built.Manager;

        await built.Fire(MakeBinaryArgs($"spBv1.0/{NcmdGroupId}/NBIRTH/{NcmdNodeId}", NbirthPayload()));
        await built.Fire(MakeBinaryArgs(
            $"spBv1.0/{NcmdGroupId}/NCMD/{NcmdNodeId}",
            NcmdPayload(SparkplugControlMetrics.NodeReboot, true)));

        topology.Groups[NcmdGroupId].Nodes[NcmdNodeId].LastRebirthRequestAt.Should().BeNull();
    }
}
