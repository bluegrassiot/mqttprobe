using System.Text;
using Google.Protobuf;
using MQTTnet;
using MqttProbe.Core.Models.Chart;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Services.Chart;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Core.Services.Sparkplug;
using MqttProbe.PluginContracts;
using MqttProbe.Tests.Utilities;
using Org.Eclipse.Tahu.Protobuf;

namespace MqttProbe.Core.Tests.Services.Chart;

[TestFixture]
public class ChartDataServiceSparkplugDatatypeTests
{
    private IMqttManagedClient _client = null!;
    private ChartDataService? _service;
    private Func<MqttApplicationMessageReceivedEventArgs, Task>? _handler;

    [SetUp]
    public void Setup()
    {
        _client = Substitute.For<IMqttManagedClient>();
        _handler = null;
        _client
            .When(x => x.ApplicationMessageReceivedAsync += Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>())
            .Do(x => _handler = x.Arg<Func<MqttApplicationMessageReceivedEventArgs, Task>>());
    }

    [TearDown]
    public void TearDown()
    {
        _service?.Dispose();
        _client.Dispose();
    }

    [Test]
    public async Task OngoingNodeData_NamedDatetimeLongValue_CollectsThroughNamedPath()
    {
        var seriesId = Guid.NewGuid();
        var settings = SettingsWith(
            new ChartSeries { Id = seriesId, Topic = "spBv1.0/g/NDATA/eon1", JsonPath = "metrics.Timestamp" });
        await BuildServiceAsync(settings, SparkplugWith(enrichAliasNames: true));

        await FireAsync("spBv1.0/g/NDATA/eon1", SpbPayload(
            new Payload.Types.Metric { Name = "Timestamp", Datatype = 13, LongValue = 1714050000000 }));

        _service!.GetPoints(seriesId).Should().ContainSingle()
            .Which.Value.Should().BeApproximately(1714050000000, 0.001);
    }

    [TestCase(1u, 128UL, -128.0)]
    [TestCase(1u, 127UL, 127.0)]
    [TestCase(1u, 255UL, -1.0)]
    [TestCase(2u, 32768UL, -32768.0)]
    [TestCase(2u, 32767UL, 32767.0)]
    [TestCase(2u, 65535UL, -1.0)]
    [TestCase(3u, 2147483648UL, -2147483648.0)]
    [TestCase(3u, 2147483647UL, 2147483647.0)]
    [TestCase(3u, 4294967295UL, -1.0)]
    [TestCase(4u, 9223372036854775808UL, -9223372036854775808.0)]
    [TestCase(4u, 9223372036854775807UL, 9223372036854775808.0)]
    [TestCase(4u, ulong.MaxValue, -1.0)]
    public async Task OngoingNodeData_SignedIntegerDatatypes_DecodeNarrowedValues(
        uint datatype, ulong rawValue, double expected)
    {
        var points = await CollectIntegerPointAsync([IntegerMetric(datatype, rawValue)]);

        points.Should().ContainSingle().Which.Value.Should().Be(expected);
    }

    [TestCase(1u, ulong.MaxValue, -1.0)]
    [TestCase(1u, 18446744073709551488UL, -128.0)]
    [TestCase(2u, 18446744073709518848UL, -32768.0)]
    public async Task OngoingNodeData_SignExtendedNarrowIntegers_DecodeNarrowedValues(
        uint datatype, ulong rawValue, double expected)
    {
        var points = await CollectIntegerPointAsync(
            [new Payload.Types.Metric { Name = "Value", Datatype = datatype, LongValue = rawValue }]);

        points.Should().ContainSingle().Which.Value.Should().Be(expected);
    }

    [TestCase(5u, 255UL, 255.0)]
    [TestCase(5u, 128UL, 128.0)]
    [TestCase(6u, 65535UL, 65535.0)]
    [TestCase(7u, 4294967295UL, 4294967295.0)]
    public async Task OngoingNodeData_UnsignedIntegerDatatypes_PreserveRawValues(
        uint datatype, ulong rawValue, double expected)
    {
        var points = await CollectIntegerPointAsync([IntegerMetric(datatype, rawValue)]);

        points.Should().ContainSingle().Which.Value.Should().Be(expected);
    }

    [Test]
    public async Task OngoingNodeData_UnsignedInt64Max_PreservesRawValue()
    {
        var points = await CollectIntegerPointAsync(
            [new Payload.Types.Metric { Name = "Value", Datatype = 8, LongValue = ulong.MaxValue }]);

        points.Should().ContainSingle().Which.Value.Should().Be((double)ulong.MaxValue);
    }

    [Test]
    public async Task OngoingNodeAndDeviceData_AliasOnlyNativeMetricsWithoutDatatype_CollectThroughNamedPaths()
    {
        var topology = new SparkplugTopologyService();
        await topology.ApplyTopologyEventsAsync(
        [
            new NodeBirthEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/NBIRTH/eon1",
                GroupId = "g",
                NodeId = "eon1",
                Metrics = [new MetricSnapshot { Name = "Flow Rate", DataType = "double", Value = "0", Alias = 42 }]
            },
            new DeviceBirthEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/DBIRTH/eon1/dev1",
                GroupId = "g",
                NodeId = "eon1",
                DeviceId = "dev1",
                Metrics = [new MetricSnapshot { Name = "Running", DataType = "bool", Value = "false", Alias = 7 }]
            }
        ]);

        var nodeSeriesId = Guid.NewGuid();
        var deviceSeriesId = Guid.NewGuid();
        var settings = SettingsWith(
            new ChartSeries { Id = nodeSeriesId, Topic = "spBv1.0/g/NDATA/eon1", JsonPath = "metrics.Flow Rate" },
            new ChartSeries
            {
                Id = deviceSeriesId,
                Topic = "spBv1.0/g/DDATA/eon1/dev1",
                JsonPath = "metrics.Running"
            });
        await BuildServiceAsync(settings, SparkplugWith(enrichAliasNames: true), topology);

        await FireAsync("spBv1.0/g/NDATA/eon1", SpbPayload(
            new Payload.Types.Metric { Alias = 42, DoubleValue = 24.5 }));
        await FireAsync("spBv1.0/g/DDATA/eon1/dev1", SpbPayload(
            new Payload.Types.Metric { Alias = 7, BooleanValue = true }));

        _service!.GetPoints(nodeSeriesId).Should().ContainSingle()
            .Which.Value.Should().BeApproximately(24.5, 0.001);
        _service.GetPoints(deviceSeriesId).Should().ContainSingle()
            .Which.Value.Should().BeApproximately(1.0, 0.001);
    }

    [Test]
    public async Task OngoingNodeData_AliasOnlyWithoutDatatype_InheritsSignedTypeFromBirth()
    {
        var topology = new SparkplugTopologyService();
        await topology.ApplyTopologyEventsAsync(
        [
            new NodeBirthEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/NBIRTH/eon1",
                GroupId = "g",
                NodeId = "eon1",
                Metrics = [new MetricSnapshot { Name = "Flow Rate", DataType = "int64", Value = "0", Alias = 42 }]
            }
        ]);

        var seriesId = Guid.NewGuid();
        var settings = SettingsWith(
            new ChartSeries { Id = seriesId, Topic = "spBv1.0/g/NDATA/eon1", JsonPath = "metrics.Flow Rate" });
        await BuildServiceAsync(settings, SparkplugWith(enrichAliasNames: true), topology);

        await FireAsync("spBv1.0/g/NDATA/eon1", SpbPayload(
            new Payload.Types.Metric { Alias = 42, LongValue = ulong.MaxValue }));

        _service!.GetPoints(seriesId).Should().ContainSingle()
            .Which.Value.Should().Be(-1.0);
    }

    [Test]
    public async Task OngoingNodeData_NameOnlyWithoutDatatype_InheritsSignedTypeFromBirth()
    {
        var topology = new SparkplugTopologyService();
        await topology.ApplyTopologyEventsAsync(
        [
            new NodeBirthEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/NBIRTH/eon1",
                GroupId = "g",
                NodeId = "eon1",
                Metrics = [new MetricSnapshot { Name = "Pressure", DataType = "int16", Value = "0" }]
            }
        ]);

        var seriesId = Guid.NewGuid();
        var settings = SettingsWith(
            new ChartSeries { Id = seriesId, Topic = "spBv1.0/g/NDATA/eon1", JsonPath = "metrics.Pressure" });
        await BuildServiceAsync(settings, SparkplugWith(enrichAliasNames: true), topology);

        await FireAsync("spBv1.0/g/NDATA/eon1", SpbPayload(
            new Payload.Types.Metric { Name = "Pressure", LongValue = 18446744073709518848UL }));

        _service!.GetPoints(seriesId).Should().ContainSingle()
            .Which.Value.Should().Be(-32768.0);
    }

    [Test]
    public async Task OngoingNodeAndDeviceData_SameAlias_UsesOwnScopeTypes()
    {
        var topology = new SparkplugTopologyService();
        await topology.ApplyTopologyEventsAsync(
        [
            new NodeBirthEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/NBIRTH/eon1",
                GroupId = "g",
                NodeId = "eon1",
                Metrics = [new MetricSnapshot { Name = "Level", DataType = "int8", Value = "0", Alias = 7 }]
            },
            new DeviceBirthEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/DBIRTH/eon1/dev1",
                GroupId = "g",
                NodeId = "eon1",
                DeviceId = "dev1",
                Metrics = [new MetricSnapshot { Name = "Level", DataType = "uint8", Value = "0", Alias = 7 }]
            }
        ]);

        var nodeSeriesId = Guid.NewGuid();
        var deviceSeriesId = Guid.NewGuid();
        var settings = SettingsWith(
            new ChartSeries { Id = nodeSeriesId, Topic = "spBv1.0/g/NDATA/eon1", JsonPath = "metrics.Level" },
            new ChartSeries
            {
                Id = deviceSeriesId,
                Topic = "spBv1.0/g/DDATA/eon1/dev1",
                JsonPath = "metrics.Level"
            });
        await BuildServiceAsync(settings, SparkplugWith(enrichAliasNames: true), topology);

        await FireAsync("spBv1.0/g/NDATA/eon1", SpbPayload(
            new Payload.Types.Metric { Alias = 7, LongValue = 255 }));
        await FireAsync("spBv1.0/g/DDATA/eon1/dev1", SpbPayload(
            new Payload.Types.Metric { Alias = 7, LongValue = 255 }));

        _service!.GetPoints(nodeSeriesId).Should().ContainSingle().Which.Value.Should().Be(-1.0);
        _service.GetPoints(deviceSeriesId).Should().ContainSingle().Which.Value.Should().Be(255.0);
    }

    [Test]
    public async Task OngoingNodeData_RebirthReplacingType_ChangesAliasOnlyDecoding()
    {
        var topology = new SparkplugTopologyService();
        await topology.ApplyTopologyEventsAsync(
        [
            new NodeBirthEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/NBIRTH/eon1",
                GroupId = "g",
                NodeId = "eon1",
                Metrics = [new MetricSnapshot { Name = "Level", DataType = "int8", Value = "0", Alias = 7 }]
            }
        ]);

        var seriesId = Guid.NewGuid();
        var settings = SettingsWith(
            new ChartSeries { Id = seriesId, Topic = "spBv1.0/g/NDATA/eon1", JsonPath = "metrics.Level" });
        await BuildServiceAsync(settings, SparkplugWith(enrichAliasNames: true), topology);

        await FireAsync("spBv1.0/g/NDATA/eon1", SpbPayload(
            new Payload.Types.Metric { Alias = 7, LongValue = 255 }));

        await topology.ApplyTopologyEventsAsync(
        [
            new NodeBirthEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/NBIRTH/eon1",
                GroupId = "g",
                NodeId = "eon1",
                Metrics = [new MetricSnapshot { Name = "Level", DataType = "uint8", Value = "0", Alias = 7 }]
            }
        ]);

        await FireAsync("spBv1.0/g/NDATA/eon1", SpbPayload(
            new Payload.Types.Metric { Alias = 7, LongValue = 255 }));

        _service!.GetPoints(seriesId).Select(point => point.Value).Should().Equal(-1.0, 255.0);
    }

    [Test]
    public async Task OngoingNodeData_AfterNodeDeath_AliasOnlyWithoutDatatype_StillInheritsBirthType()
    {
        var topology = new SparkplugTopologyService();
        await topology.ApplyTopologyEventsAsync(
        [
            new NodeBirthEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/NBIRTH/eon1",
                GroupId = "g",
                NodeId = "eon1",
                Metrics = [new MetricSnapshot { Name = "Level", DataType = "int8", Value = "0", Alias = 7 }]
            },
            new NodeDeathEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/NDEATH/eon1",
                GroupId = "g",
                NodeId = "eon1"
            }
        ]);

        var seriesId = Guid.NewGuid();
        var settings = SettingsWith(
            new ChartSeries { Id = seriesId, Topic = "spBv1.0/g/NDATA/eon1", JsonPath = "metrics.Level" });
        await BuildServiceAsync(settings, SparkplugWith(enrichAliasNames: true), topology);

        await FireAsync("spBv1.0/g/NDATA/eon1", SpbPayload(
            new Payload.Types.Metric { Alias = 7, LongValue = 255 }));

        _service!.GetPoints(seriesId).Should().ContainSingle().Which.Value.Should().Be(-1.0);
    }

    [Test]
    public async Task OngoingDeviceData_AfterDeviceDeath_AliasOnlyWithoutDatatype_StillInheritsBirthType()
    {
        var topology = new SparkplugTopologyService();
        await topology.ApplyTopologyEventsAsync(
        [
            new DeviceBirthEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/DBIRTH/eon1/dev1",
                GroupId = "g",
                NodeId = "eon1",
                DeviceId = "dev1",
                Metrics = [new MetricSnapshot { Name = "Level", DataType = "int8", Value = "0", Alias = 7 }]
            },
            new DeviceDeathEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/DDEATH/eon1/dev1",
                GroupId = "g",
                NodeId = "eon1",
                DeviceId = "dev1"
            }
        ]);

        var seriesId = Guid.NewGuid();
        var settings = SettingsWith(
            new ChartSeries
            {
                Id = seriesId,
                Topic = "spBv1.0/g/DDATA/eon1/dev1",
                JsonPath = "metrics.Level"
            });
        await BuildServiceAsync(settings, SparkplugWith(enrichAliasNames: true), topology);

        await FireAsync("spBv1.0/g/DDATA/eon1/dev1", SpbPayload(
            new Payload.Types.Metric { Alias = 7, LongValue = 255 }));

        _service!.GetPoints(seriesId).Should().ContainSingle().Which.Value.Should().Be(-1.0);
    }

    [Test]
    public async Task OngoingNodeData_AfterNodeRemoval_NameOnlyWithoutDatatype_FallsBackToRaw()
    {
        var topology = new SparkplugTopologyService();
        await topology.ApplyTopologyEventsAsync(
        [
            new NodeBirthEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/NBIRTH/eon1",
                GroupId = "g",
                NodeId = "eon1",
                Metrics = [new MetricSnapshot { Name = "Level", DataType = "int8", Value = "0" }]
            }
        ]);
        topology.RemoveNode("g", "eon1");

        var seriesId = Guid.NewGuid();
        var settings = SettingsWith(
            new ChartSeries { Id = seriesId, Topic = "spBv1.0/g/NDATA/eon1", JsonPath = "metrics.Level" });
        await BuildServiceAsync(settings, SparkplugWith(enrichAliasNames: true), topology);

        await FireAsync("spBv1.0/g/NDATA/eon1", SpbPayload(
            new Payload.Types.Metric { Name = "Level", LongValue = 255 }));

        _service!.GetPoints(seriesId).Should().ContainSingle().Which.Value.Should().Be(255.0);
    }

    [Test]
    public async Task PlainJson_IntegerValueFieldsWithoutSparkplugContext_StayRaw()
    {
        var seriesId = Guid.NewGuid();
        var settings = SettingsWith(
            new ChartSeries { Id = seriesId, Topic = "sensor/plain", JsonPath = "metrics.Value" });
        await BuildServiceAsync(settings, SparkplugWith(enrichAliasNames: false));

        await FireAsync("sensor/plain",
            Encoding.UTF8.GetBytes("""{"metrics":[{"name":"Value","datatype":3,"intValue":4294967295}]}"""));

        _service!.GetPoints(seriesId).Should().ContainSingle()
            .Which.Value.Should().Be(4294967295.0);
    }

    private static Payload.Types.Metric IntegerMetric(uint datatype, ulong rawValue)
    {
        if (datatype is >= 1 and <= 3 or >= 5 and <= 7)
            return new Payload.Types.Metric { Name = "Value", Datatype = datatype, IntValue = (uint)rawValue };

        return new Payload.Types.Metric { Name = "Value", Datatype = datatype, LongValue = rawValue };
    }

    private async Task<IReadOnlyList<ChartDataPoint>> CollectIntegerPointAsync(Payload.Types.Metric[] metrics)
    {
        var seriesId = Guid.NewGuid();
        var settings = SettingsWith(
            new ChartSeries { Id = seriesId, Topic = "spBv1.0/g/NDATA/eon1", JsonPath = "metrics.Value" });
        await BuildServiceAsync(settings, SparkplugWith(enrichAliasNames: false));

        await FireAsync("spBv1.0/g/NDATA/eon1", SpbPayload(metrics));

        return _service!.GetPoints(seriesId);
    }

    private async Task BuildServiceAsync(
        IChartSettings chartSettings,
        ISparkplugSettings sparkplugSettings,
        ISparkplugTopologyService? topology = null)
    {
        _service = new ChartDataService(
            _client,
            new JsonFieldExtractor(),
            new ChartFieldRegistry(),
            chartSettings,
            TestPipelineHelper.BuildBuiltInPipeline(),
            sparkplugSettings,
            topology);
        await _service.StartAsync();
    }

    private Task FireAsync(string topic, byte[] payload) => _handler!(MakeBinaryArgs(topic, payload));

    private static IChartSettings SettingsWith(params ChartSeries[] series)
    {
        var settings = Substitute.For<IChartSettings>();
        settings.GetCharts(Arg.Any<Guid>()).Returns(
        [
            new ChartConfiguration { MaxPoints = 100, Series = [.. series] }
        ]);
        return settings;
    }

    private static ISparkplugSettings SparkplugWith(bool enrichAliasNames)
    {
        var settings = Substitute.For<ISparkplugSettings>();
        settings.Sparkplug.Returns(new SparkplugSettings { EnrichAliasNames = enrichAliasNames });
        return settings;
    }

    private static byte[] SpbPayload(params Payload.Types.Metric[] metrics)
    {
        var payload = new Payload { Timestamp = 2 };
        payload.Metrics.AddRange(metrics);
        return payload.ToByteArray();
    }

    private static MqttApplicationMessageReceivedEventArgs MakeBinaryArgs(string topic, byte[] payload)
    {
        var appMsg = new MqttApplicationMessageBuilder()
            .WithTopic(topic).WithPayload(payload).Build();
        var packet = new MQTTnet.Packets.MqttPublishPacket { Topic = topic };
        return new MqttApplicationMessageReceivedEventArgs("test-client", appMsg, packet, null);
    }
}
