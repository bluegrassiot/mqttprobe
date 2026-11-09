using Google.Protobuf;
using MqttProbe.Core.Models.Sparkplug;
using MqttProbe.Core.Services.Sparkplug;
using MqttProbe.PluginContracts;
using Org.Eclipse.Tahu.Protobuf;

namespace MqttProbe.Core.Tests.Services.Sparkplug;

[TestFixture]
public sealed class SparkplugAliasResolverMetricNameTests
{
    private const string Topic = "spBv1.0/group/NDATA/eon1";

    [Test]
    public void ResolveMetricNames_TypedPayload_ReturnsNamedAndAliasResolvedNames()
    {
        var bytes = PayloadBytes(("Pressure", 0), (null, 7), ("Pressure", 7));

        var result = SparkplugAliasResolver.ResolveMetricNames(
            Topic, Envelope(bytes, Payload.Parser.ParseFrom(bytes)), GroupsWithNodeAlias(7, "Temperature"));

        result.Should().Equal("Pressure", "Temperature");
    }

    [Test]
    public void ResolveMetricNames_NullTypedPayload_FallsBackToRawPayload()
    {
        var result = SparkplugAliasResolver.ResolveMetricNames(
            Topic, Envelope(PayloadBytes((null, 7))), GroupsWithNodeAlias(7, "Temperature"));

        result.Should().Equal("Temperature");
    }

    [Test]
    public void ResolveMetricNames_ForeignTypedPayload_FallsBackToRawPayload()
    {
        var result = SparkplugAliasResolver.ResolveMetricNames(
            Topic, Envelope(PayloadBytes((null, 7)), new object()), GroupsWithNodeAlias(7, "Temperature"));

        result.Should().Equal("Temperature");
    }

    [Test]
    public void ResolveMetricNames_NamedMetricsWorkWithoutTopology()
    {
        var result = SparkplugAliasResolver.ResolveMetricNames(
            Topic, Envelope(PayloadBytes(("A", 0), ("A/B", 0), ("a", 0), ("A", 0))), null);

        result.Should().Equal("A", "A/B", "a");
    }

    [Test]
    public void ResolveMetricNames_UnknownAliasIsOmitted()
    {
        var result = SparkplugAliasResolver.ResolveMetricNames(
            Topic, Envelope(PayloadBytes((null, 99), ("Known", 0))), GroupsWithNodeAlias(7, "Temperature"));

        result.Should().Equal("Known");
    }

    [Test]
    public void ResolveMetricNames_MissingDeviceDoesNotBorrowNodeAliases()
    {
        var groups = GroupsWithNodeAlias(10, "NodeTemperature");

        var result = SparkplugAliasResolver.ResolveMetricNames(
            "spBv1.0/group/DDATA/eon1/missing", Envelope(PayloadBytes((null, 10))), groups);

        result.Should().BeEmpty();
    }

    [Test]
    public void ResolveMetricNames_DeviceAliasResolvesFromTheExactDevice()
    {
        var groups = GroupsWithDeviceAlias(10, "DeviceTemperature");
        groups["group"].Nodes["eon1"].AliasMap[10] = "NodeTemperature";

        var result = SparkplugAliasResolver.ResolveMetricNames(
            "spBv1.0/group/DDATA/eon1/dev1", Envelope(PayloadBytes((null, 10))), groups);

        result.Should().Equal("DeviceTemperature");
    }

    [Test]
    public async Task ResolveMetricNames_BirthReplacesTheOwnerMapAndDeathRetainsIt()
    {
        var topology = new SparkplugTopologyService();
        var aliasOnly = Envelope(PayloadBytes((null, 7)));

        await topology.ApplyTopologyEventsAsync([Birth("Alias7A")]);
        var first = SparkplugAliasResolver.ResolveMetricNames(Topic, aliasOnly, topology.Groups);

        await topology.ApplyTopologyEventsAsync([Birth("Alias7B")]);
        var reborn = SparkplugAliasResolver.ResolveMetricNames(Topic, aliasOnly, topology.Groups);

        await topology.ApplyTopologyEventsAsync(
        [
            new NodeDeathEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/group/NDEATH/eon1",
                GroupId = "group",
                NodeId = "eon1"
            }
        ]);
        var afterDeath = SparkplugAliasResolver.ResolveMetricNames(Topic, aliasOnly, topology.Groups);

        first.Should().Equal(new[] { "Alias7A" },
            "historical captures keep the names they were stored with");
        reborn.Should().Equal("Alias7B");
        afterDeath.Should().Equal("Alias7B");
    }

    [Test]
    public void ResolveMetricNames_MalformedStateUnknownShapeOrFailureIsEmpty()
    {
        var valid = PayloadBytes(("A", 0));

        SparkplugAliasResolver.ResolveMetricNames(Topic, Envelope([0x01, 0x02, 0x03]), null)
            .Should().BeEmpty();
        SparkplugAliasResolver.ResolveMetricNames("spBv1.0/group/STATE/scada", Envelope(valid), null)
            .Should().BeEmpty();
        SparkplugAliasResolver.ResolveMetricNames("sensors/temp", Envelope(valid), null)
            .Should().BeEmpty();
        SparkplugAliasResolver.ResolveMetricNames(Topic,
                DecodedPayloadEnvelope.CreateSuccess("json", Topic, valid, "{}"), null)
            .Should().BeEmpty();
        SparkplugAliasResolver.ResolveMetricNames(Topic,
                DecodedPayloadEnvelope.CreateFailure("sparkplug-b", Topic, valid, "bad"), null)
            .Should().BeEmpty();
        SparkplugAliasResolver.ResolveMetricNames(Topic, Envelope(PayloadBytes()), null)
            .Should().BeEmpty();
    }

    private static byte[] PayloadBytes(params (string? Name, ulong Alias)[] metrics)
    {
        var payload = new Payload { Timestamp = 1 };
        foreach (var (name, alias) in metrics)
        {
            var metric = new Payload.Types.Metric { Datatype = 10, DoubleValue = 1.0 };
            if (name is not null)
                metric.Name = name;
            if (alias != 0)
                metric.Alias = alias;
            payload.Metrics.Add(metric);
        }

        return payload.ToByteArray();
    }

    private static DecodedPayloadEnvelope Envelope(byte[] raw, object? typedPayload = null) =>
        DecodedPayloadEnvelope.CreateSuccess("sparkplug-b", Topic, raw, "{}", typedPayload);

    private static Dictionary<string, SpbGroup> GroupsWithNodeAlias(ulong alias, string name)
    {
        var node = new SpbNode { NodeId = "eon1", GroupId = "group" };
        node.AliasMap[alias] = name;
        var group = new SpbGroup { GroupId = "group" };
        group.Nodes["eon1"] = node;
        return new Dictionary<string, SpbGroup> { ["group"] = group };
    }

    private static Dictionary<string, SpbGroup> GroupsWithDeviceAlias(ulong alias, string name)
    {
        var device = new SpbDevice { DeviceId = "dev1", NodeId = "eon1", GroupId = "group" };
        device.AliasMap[alias] = name;
        var node = new SpbNode { NodeId = "eon1", GroupId = "group" };
        node.Devices["dev1"] = device;
        var group = new SpbGroup { GroupId = "group" };
        group.Nodes["eon1"] = node;
        return new Dictionary<string, SpbGroup> { ["group"] = group };
    }

    private static NodeBirthEvent Birth(string name) => new()
    {
        FormatId = "sparkplug-b",
        Topic = "spBv1.0/group/NBIRTH/eon1",
        GroupId = "group",
        NodeId = "eon1",
        Metrics = [new MetricSnapshot { Name = name, DataType = "double", Value = "1.0", Alias = 7 }]
    };
}
