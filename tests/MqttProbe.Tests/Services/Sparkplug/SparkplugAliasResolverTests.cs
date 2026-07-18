using Google.Protobuf;
using MqttProbe.Models.Sparkplug;
using MqttProbe.Services.Sparkplug;
using Org.Eclipse.Tahu.Protobuf;

namespace MqttProbe.Shared.Tests.Services.Sparkplug;

[TestFixture]
public class SparkplugAliasResolverTests
{
    private static byte[] BuildSparkplugPayload(string? name, ulong alias, double doubleValue)
    {
        var payload = new Payload { Timestamp = 1234567890 };
        var metric = new Payload.Types.Metric
        {
            Datatype = 10,
            DoubleValue = doubleValue
        };
        if (name is not null)
            metric.Name = name;
        if (alias != 0)
            metric.Alias = alias;
        payload.Metrics.Add(metric);
        return payload.ToByteArray();
    }

    [Test]
    public void Resolve_AliasOnlyMetric_ResolvesName()
    {
        var node = new SpbNode { NodeId = "eon1", GroupId = "group" };
        node.AliasMap[42] = "Flow Rate";
        var group = new SpbGroup { GroupId = "group" };
        group.Nodes["eon1"] = node;
        var groups = new Dictionary<string, SpbGroup> { ["group"] = group };

        var bytes = BuildSparkplugPayload(null, 42, 3.14);
        var result = SparkplugAliasResolver.Resolve("spBv1.0/group/NDATA/eon1", bytes, groups);

        result.Should().NotBeNull();
        result![42].Should().Be("Flow Rate");
    }

    [Test]
    public void Resolve_MissingAliasMapping_ReturnsNull()
    {
        var node = new SpbNode { NodeId = "eon1", GroupId = "group" };
        node.AliasMap[7] = "Temperature";
        var group = new SpbGroup { GroupId = "group" };
        group.Nodes["eon1"] = node;
        var groups = new Dictionary<string, SpbGroup> { ["group"] = group };

        var bytes = BuildSparkplugPayload(null, 99, 1.0);
        var result = SparkplugAliasResolver.Resolve("spBv1.0/group/NDATA/eon1", bytes, groups);

        result.Should().BeNull();
    }

    [Test]
    public void Resolve_NamedMetric_NotIncludedInAliasNames()
    {
        var node = new SpbNode { NodeId = "eon1", GroupId = "group" };
        node.AliasMap[5] = "Pressure";
        var group = new SpbGroup { GroupId = "group" };
        group.Nodes["eon1"] = node;
        var groups = new Dictionary<string, SpbGroup> { ["group"] = group };

        var bytes = BuildSparkplugPayload("Pressure", 5, 1013.25);
        var result = SparkplugAliasResolver.Resolve("spBv1.0/group/NBIRTH/eon1", bytes, groups);

        result.Should().BeNull();
    }

    [Test]
    public void Resolve_NonSparkplugTopic_ReturnsNull()
    {
        var groups = new Dictionary<string, SpbGroup>();
        var bytes = BuildSparkplugPayload(null, 42, 3.14);
        var result = SparkplugAliasResolver.Resolve("sensor/temp", bytes, groups);

        result.Should().BeNull();
    }

    [Test]
    public void Resolve_TopologyNotInitialized_ReturnsNull()
    {
        var groups = new Dictionary<string, SpbGroup>();
        var bytes = BuildSparkplugPayload(null, 42, 3.14);
        var result = SparkplugAliasResolver.Resolve("spBv1.0/group/NDATA/eon1", bytes, groups);

        result.Should().BeNull();
    }

    [Test]
    public void Resolve_InvalidProtobufBytes_ReturnsNull()
    {
        var groups = new Dictionary<string, SpbGroup>();
        var result = SparkplugAliasResolver.Resolve(
            "spBv1.0/group/NDATA/eon1",
            new byte[] { 0x01, 0x02, 0x03 },
            groups);

        result.Should().BeNull();
    }

    [Test]
    public void Resolve_DeviceAlias_ResolvesFromDeviceMap()
    {
        var device = new SpbDevice { DeviceId = "dev1", NodeId = "eon1", GroupId = "group" };
        device.AliasMap[10] = "Temperature";
        var node = new SpbNode { NodeId = "eon1", GroupId = "group" };
        node.Devices["dev1"] = device;
        var group = new SpbGroup { GroupId = "group" };
        group.Nodes["eon1"] = node;
        var groups = new Dictionary<string, SpbGroup> { ["group"] = group };

        var bytes = BuildSparkplugPayload(null, 10, 25.5);
        var result = SparkplugAliasResolver.Resolve("spBv1.0/group/DDATA/eon1/dev1", bytes, groups);

        result.Should().NotBeNull();
        result![10].Should().Be("Temperature");
    }

    [Test]
    public void Resolve_EmptyPayload_ReturnsNull()
    {
        var groups = new Dictionary<string, SpbGroup>();
        var result = SparkplugAliasResolver.Resolve("spBv1.0/group/NDATA/eon1", [], groups);

        result.Should().BeNull();
    }
}
