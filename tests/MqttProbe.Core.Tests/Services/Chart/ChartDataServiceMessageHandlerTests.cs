using Google.Protobuf;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MqttProbe.Core.Models.Chart;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Chart;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Core.Services.Sparkplug;
using MqttProbe.PluginContracts;
using MqttProbe.Tests.Utilities;
using Org.Eclipse.Tahu.Protobuf;

namespace MqttProbe.Core.Tests.Services.Chart;

[TestFixture]
public class ChartDataServiceMessageHandlerTests
{
    private IMqttManagedClient _mockClient = null!;
    private ChartFieldRegistry _registry = null!;
    private IChartSettings _mockChartSettings = null!;
    private ChartDataService _service = null!;
    private Func<MqttApplicationMessageReceivedEventArgs, Task>? _handler;

    [SetUp]
    public async Task Setup()
    {
        _mockClient = Substitute.For<IMqttManagedClient>();
        _registry = new ChartFieldRegistry();
        _mockChartSettings = Substitute.For<IChartSettings>();
        _mockChartSettings.GetCharts(Arg.Any<Guid>()).Returns([]);
        _service = new ChartDataService(_mockClient, new JsonFieldExtractor(), _registry, _mockChartSettings,
            TestPipelineHelper.BuildBuiltInPipeline(), Substitute.For<ISparkplugSettings>());

        _handler = null;
        _mockClient
            .When(x => x.ApplicationMessageReceivedAsync += Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>())
            .Do(x => _handler = x.Arg<Func<MqttApplicationMessageReceivedEventArgs, Task>>());

        await _service.StartAsync();
    }

    [TearDown]
    public void TearDown()
    {
        _service.Dispose();
        _mockClient.Dispose();
    }

    private static MqttApplicationMessageReceivedEventArgs MakeArgs(string topic, string payload)
    {
        var appMsg = new MqttApplicationMessageBuilder()
            .WithTopic(topic).WithPayload(payload).Build();
        var packet = new MQTTnet.Packets.MqttPublishPacket { Topic = topic };
        return new MqttApplicationMessageReceivedEventArgs("test-client", appMsg, packet, null);
    }

    private Task Fire(string topic, string payload) => _handler!(MakeArgs(topic, payload));

    private static ChartConfiguration ConfigWith(int maxPoints, params ChartSeries[] series) =>
        new() { MaxPoints = maxPoints, Series = [.. series] };

    [Test]
    public async Task MatchingTopicAndPath_AddsPointToBuffer()
    {
        var seriesId = Guid.NewGuid();
        _mockChartSettings.GetCharts(Arg.Any<Guid>()).Returns(
        [
            ConfigWith(100, new ChartSeries { Id = seriesId, Topic = "sensor/temp", JsonPath = "temperature" })
        ]);

        await Fire("sensor/temp", """{"temperature": 21.5}""");

        _service.GetPoints(seriesId).Should().ContainSingle()
            .Which.Value.Should().BeApproximately(21.5, 0.001);
    }

    [Test]
    public async Task NonMatchingTopic_NoPointAdded()
    {
        var seriesId = Guid.NewGuid();
        _mockChartSettings.GetCharts(Arg.Any<Guid>()).Returns(
        [
            ConfigWith(100, new ChartSeries { Id = seriesId, Topic = "sensor/temp", JsonPath = "temperature" })
        ]);

        await Fire("sensor/humidity", """{"temperature": 21.5}""");

        _service.GetPoints(seriesId).Should().BeEmpty();
    }

    [Test]
    public async Task NonMatchingJsonPath_NoPointAdded()
    {
        var seriesId = Guid.NewGuid();
        _mockChartSettings.GetCharts(Arg.Any<Guid>()).Returns(
        [
            ConfigWith(100, new ChartSeries { Id = seriesId, Topic = "sensor/temp", JsonPath = "pressure" })
        ]);

        await Fire("sensor/temp", """{"temperature": 21.5}""");

        _service.GetPoints(seriesId).Should().BeEmpty();
    }

    [Test]
    public async Task MatchingMessage_OnDataUpdated_Fires()
    {
        var seriesId = Guid.NewGuid();
        _mockChartSettings.GetCharts(Arg.Any<Guid>()).Returns(
        [
            ConfigWith(100, new ChartSeries { Id = seriesId, Topic = "t", JsonPath = "v" })
        ]);

        var fired = false;
        _service.OnDataUpdated += () => fired = true;

        await Fire("t", """{"v": 1.0}""");

        fired.Should().BeTrue();
    }

    [Test]
    public async Task NoMatchingSeries_OnDataUpdated_DoesNotFire()
    {
        _mockChartSettings.GetCharts(Arg.Any<Guid>()).Returns([]);

        var fired = false;
        _service.OnDataUpdated += () => fired = true;

        await Fire("t", """{"v": 1.0}""");

        fired.Should().BeFalse();
    }

    [Test]
    public async Task BufferExceedsMaxPoints_OldestPointRemoved()
    {
        var seriesId = Guid.NewGuid();
        _mockChartSettings.GetCharts(Arg.Any<Guid>()).Returns(
        [
            ConfigWith(3, new ChartSeries { Id = seriesId, Topic = "t", JsonPath = "v" })
        ]);

        for (var i = 1; i <= 5; i++)
            await Fire("t", $$"""{"v": {{i}}.0}""");

        var points = _service.GetPoints(seriesId);
        points.Should().HaveCount(3);
        points.Select(p => p.Value).Should().NotContain(1.0).And.NotContain(2.0);
    }

    [Test]
    public async Task EmptyPayload_NoPointAdded()
    {
        var seriesId = Guid.NewGuid();
        _mockChartSettings.GetCharts(Arg.Any<Guid>()).Returns(
        [
            ConfigWith(100, new ChartSeries { Id = seriesId, Topic = "t", JsonPath = "v" })
        ]);

        await Fire("t", "");

        _service.GetPoints(seriesId).Should().BeEmpty();
    }

    [Test]
    public async Task InvalidJson_DoesNotThrow()
    {
        _mockChartSettings.GetCharts(Arg.Any<Guid>()).Returns([]);

        var act = async () => await Fire("t", "not json at all!@#");
        await act.Should().NotThrowAsync();
    }

    [Test]
    public async Task MatchingMessage_UpdatesFieldRegistry()
    {
        _mockChartSettings.GetCharts(Arg.Any<Guid>()).Returns([]);

        await Fire("sensor/data", """{"temp": 22.0, "humidity": 65.0}""");

        _registry.GetTopics().Should().Contain("sensor/data");
        _registry.GetFields("sensor/data").Should().HaveCount(2);
    }

    [Test]
    public async Task MultipleSeriesOnSameTopic_AllBuffersUpdated()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        _mockChartSettings.GetCharts(Arg.Any<Guid>()).Returns(
        [
            ConfigWith(100,
                new ChartSeries { Id = id1, Topic = "sensor", JsonPath = "temp" },
                new ChartSeries { Id = id2, Topic = "sensor", JsonPath = "humidity" })
        ]);

        await Fire("sensor", """{"temp": 22.0, "humidity": 65.0}""");

        _service.GetPoints(id1).Should().ContainSingle().Which.Value.Should().BeApproximately(22.0, 0.001);
        _service.GetPoints(id2).Should().ContainSingle().Which.Value.Should().BeApproximately(65.0, 0.001);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task SeparateMatchingCharts_EachReceivesPointAndNotifiesOnce(bool reverseOrder)
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var charts = new List<ChartConfiguration>
        {
            ConfigWith(100, new ChartSeries { Id = firstId, Topic = "sensors/test", JsonPath = "temperature" }),
            ConfigWith(100, new ChartSeries { Id = secondId, Topic = "sensors/test", JsonPath = "temperature" }),
            ConfigWith(100, new ChartSeries { Id = otherId, Topic = "sensors/other", JsonPath = "temperature" })
        };
        if (reverseOrder)
            charts.Reverse();
        _mockChartSettings.GetCharts(Arg.Any<Guid>()).Returns(charts);

        var notifications = 0;
        _service.OnDataUpdated += () => notifications++;

        await Fire("sensors/test", """{"temperature": 21.0}""");

        _service.GetPoints(firstId).Should().ContainSingle().Which.Value.Should().BeApproximately(21.0, 0.001);
        _service.GetPoints(secondId).Should().ContainSingle().Which.Value.Should().BeApproximately(21.0, 0.001);
        _service.GetPoints(otherId).Should().BeEmpty();
        notifications.Should().Be(1);
    }

    [Test]
    public async Task SeparateMatchingChartsWithDifferentLimits_EachBufferKeepsItsOwnLimit()
    {
        var shortId = Guid.NewGuid();
        var longId = Guid.NewGuid();
        _mockChartSettings.GetCharts(Arg.Any<Guid>()).Returns(
        [
            ConfigWith(2, new ChartSeries { Id = shortId, Topic = "sensors/test", JsonPath = "temperature" }),
            ConfigWith(3, new ChartSeries { Id = longId, Topic = "sensors/test", JsonPath = "temperature" })
        ]);

        for (var i = 1; i <= 4; i++)
            await Fire("sensors/test", $$"""{"temperature": {{i}}.0}""");

        _service.GetPoints(shortId).Select(p => p.Value).Should().Equal(3.0, 4.0);
        _service.GetPoints(longId).Select(p => p.Value).Should().Equal(2.0, 3.0, 4.0);
    }

    [Test]
    public async Task MultipleMessages_PointsAccumulate()
    {
        var seriesId = Guid.NewGuid();
        _mockChartSettings.GetCharts(Arg.Any<Guid>()).Returns(
        [
            ConfigWith(100, new ChartSeries { Id = seriesId, Topic = "t", JsonPath = "v" })
        ]);

        await Fire("t", """{"v": 1.0}""");
        await Fire("t", """{"v": 2.0}""");
        await Fire("t", """{"v": 3.0}""");

        _service.GetPoints(seriesId).Should().HaveCount(3);
    }

    [Test]
    public async Task NonNumericJsonField_NoPointAdded()
    {
        var seriesId = Guid.NewGuid();
        _mockChartSettings.GetCharts(Arg.Any<Guid>()).Returns(
        [
            ConfigWith(100, new ChartSeries { Id = seriesId, Topic = "t", JsonPath = "name" })
        ]);

        await Fire("t", """{"name": "sensor-1", "temp": 21.5}""");

        _service.GetPoints(seriesId).Should().BeEmpty();
    }

    [Test]
    public async Task MessageHandler_WhenRegistryThrows_LogsAndDoesNotPropagate()
    {
        var client = Substitute.For<IMqttManagedClient>();
        var registry = Substitute.For<IChartFieldRegistry>();
        registry.When(x => x.Update(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, ExtractedField>>()))
            .Do(_ => throw new InvalidOperationException("registry failed"));
        var configStore = Substitute.For<IChartSettings>();
        configStore.GetCharts(Arg.Any<Guid>()).Returns([]);
        var logger = new CapturingLogger<ChartDataService>();
        Func<MqttApplicationMessageReceivedEventArgs, Task>? handler = null;
        client
            .When(x => x.ApplicationMessageReceivedAsync += Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>())
            .Do(x => handler = x.Arg<Func<MqttApplicationMessageReceivedEventArgs, Task>>());

        using var service = new ChartDataService(
            client,
            new JsonFieldExtractor(),
            registry,
            configStore,
            TestPipelineHelper.BuildBuiltInPipeline(),
            Substitute.For<ISparkplugSettings>(),
            logger: logger);
        await service.StartAsync();

        var act = async () => await handler!(MakeArgs("sensor/data", """{"temp":22.0}"""));

        await act.Should().NotThrowAsync();
        logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Error &&
            entry.Message.Contains("Error processing chart data message", StringComparison.Ordinal) &&
            entry.Exception != null);
    }

    [Test]
    public async Task SetConnection_WithMatchingConfig_BuffersPointsForActiveConnection()
    {
        var connectionId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        _mockChartSettings.GetCharts(connectionId).Returns(
        [
            ConfigWith(100, new ChartSeries { Id = seriesId, Topic = "sensor/temp", JsonPath = "temperature" })
        ]);

        _service.SetConnection(connectionId);
        await Fire("sensor/temp", """{"temperature": 21.5}""");

        _service.GetPoints(seriesId).Should().ContainSingle()
            .Which.Value.Should().BeApproximately(21.5, 0.001);
    }

    [Test]
    public async Task TopicExclusion_PurgesRegistryAndBuffersAndGatesFutureMessages()
    {
        var client = Substitute.For<IMqttManagedClient>();
        var registry = new ChartFieldRegistry();
        var settings = Substitute.For<IChartSettings>();
        var connection = Guid.NewGuid();
        var series = Guid.NewGuid();
        settings.GetCharts(connection).Returns([
            ConfigWith(100, new ChartSeries { Id = series, Topic = "sensors/temp", JsonPath = "value" })]);

        var session = new SessionState { SelectedConnection = new Connection() };
        var excludes = new TopicExcludeService(
            Substitute.For<ILogger<TopicExcludeService>>(),
            Substitute.For<IConnectionSettings>(), session);
        Func<MqttApplicationMessageReceivedEventArgs, Task>? handler = null;
        client.When(x => x.ApplicationMessageReceivedAsync += Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>())
            .Do(x => handler = x.Arg<Func<MqttApplicationMessageReceivedEventArgs, Task>>());

        using var service = new ChartDataService(client, new JsonFieldExtractor(), registry, settings,
            TestPipelineHelper.BuildBuiltInPipeline(), Substitute.For<ISparkplugSettings>(),
            topicExcludeService: excludes);
        service.SetConnection(connection);
        await service.StartAsync();
        await handler!(MakeArgs("sensors/temp", "{\"value\": 1}"));

        await excludes.Add("sensors/#");

        registry.GetTopics().Should().BeEmpty();
        service.GetPoints(series).Should().BeEmpty();
        await handler!(MakeArgs("sensors/temp", "{\"value\": 2}"));
        service.GetPoints(series).Should().BeEmpty();
        excludes.Dispose();
    }
    [Test]
    public async Task TopicExclusion_AddWaitsForInFlightHandler_ThenPurgesRegistryAndBuffers()
    {
        var client = Substitute.For<IMqttManagedClient>();
        var registry = new ChartFieldRegistry();
        var settings = Substitute.For<IChartSettings>();
        var connection = Guid.NewGuid();
        var series = Guid.NewGuid();
        settings.GetCharts(connection).Returns([
            ConfigWith(100, new ChartSeries { Id = series, Topic = "sensors/temp", JsonPath = "value" })]);

        var session = new SessionState { SelectedConnection = new Connection() };
        using var excludes = new TopicExcludeService(
            Substitute.For<ILogger<TopicExcludeService>>(),
            Substitute.For<IConnectionSettings>(), session);
        Func<MqttApplicationMessageReceivedEventArgs, Task>? handler = null;
        client.When(x => x.ApplicationMessageReceivedAsync += Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>())
            .Do(x => handler = x.Arg<Func<MqttApplicationMessageReceivedEventArgs, Task>>());

        var extractor = new BarrierFieldExtractor();
        using var service = new ChartDataService(client, extractor, registry, settings,
            TestPipelineHelper.BuildBuiltInPipeline(), Substitute.For<ISparkplugSettings>(),
            topicExcludeService: excludes);
        service.SetConnection(connection);
        await service.StartAsync();

        var fireTask = Task.Run(() => handler!(MakeArgs("sensors/temp", """{"value": 1}""")));
        Task<TopicExcludeOperationResult>? addTask;
        bool filterApplied;
        bool finishedWhileParked;
        try
        {
            (await SatisfiesAsync(() => extractor.Reached, TimeSpan.FromSeconds(5))).Should().BeTrue(
                "the handler has to be parked inside the exclusion read gate before Add starts");
            addTask = excludes.Add("sensors/#");
            filterApplied = await SatisfiesAsync(
                () => excludes.IsExcluded("sensors/temp"), TimeSpan.FromSeconds(2));
            finishedWhileParked = await SatisfiesAsync(
                () => addTask.IsCompleted, TimeSpan.FromMilliseconds(250));
        }
        finally
        {
            extractor.Release();
        }

        var result = await addTask.WaitAsync(TimeSpan.FromSeconds(10));
        await fireTask.WaitAsync(TimeSpan.FromSeconds(10));

        filterApplied.Should().BeTrue("the filter has to be live before the purge waits for readers");
        finishedWhileParked.Should().BeFalse(
            "Add must not purge while the handler is in flight, or the handler repopulates the registry and buffer");
        result.IsValid.Should().BeTrue();
        registry.GetTopics().Should().BeEmpty();
        service.GetPoints(series).Should().BeEmpty();
    }

    [Test]
    public async Task OnDataUpdated_HandlerAddingExclusion_CompletesWithoutDeadlock()
    {
        var client = Substitute.For<IMqttManagedClient>();
        var registry = new ChartFieldRegistry();
        var settings = Substitute.For<IChartSettings>();
        var connection = Guid.NewGuid();
        var series = Guid.NewGuid();
        settings.GetCharts(connection).Returns([
            ConfigWith(100, new ChartSeries { Id = series, Topic = "sensors/temp", JsonPath = "value" })]);

        var session = new SessionState { SelectedConnection = new Connection() };
        using var excludes = new TopicExcludeService(
            Substitute.For<ILogger<TopicExcludeService>>(),
            Substitute.For<IConnectionSettings>(), session);
        Func<MqttApplicationMessageReceivedEventArgs, Task>? handler = null;
        client.When(x => x.ApplicationMessageReceivedAsync += Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>())
            .Do(x => handler = x.Arg<Func<MqttApplicationMessageReceivedEventArgs, Task>>());

        using var service = new ChartDataService(client, new JsonFieldExtractor(), registry, settings,
            TestPipelineHelper.BuildBuiltInPipeline(), Substitute.For<ISparkplugSettings>(),
            topicExcludeService: excludes);
        service.SetConnection(connection);
        await service.StartAsync();

        var probed = 0;
        service.OnDataUpdated += () =>
        {
            // The purge re-enters this handler; only the first call probes the gate.
            if (Interlocked.Exchange(ref probed, 1) != 0)
                return;

            _ = excludes.Add("sensors/#").GetAwaiter().GetResult();
        };

        var fireTask = Task.Run(() => handler!(MakeArgs("sensors/temp", """{"value": 1}""")));

        await fireTask.WaitAsync(TimeSpan.FromSeconds(10));

        excludes.IsExcluded("sensors/temp").Should().BeTrue();
        registry.GetTopics().Should().BeEmpty();
        service.GetPoints(series).Should().BeEmpty();
    }

    [Test]
    public async Task ExclusionAddedWhileStopped_LeavesNoExcludedDataOnRestart()
    {
        var client = Substitute.For<IMqttManagedClient>();
        var registry = new ChartFieldRegistry();
        var settings = Substitute.For<IChartSettings>();
        var connection = Guid.NewGuid();
        var series = Guid.NewGuid();
        settings.GetCharts(connection).Returns([
            ConfigWith(100, new ChartSeries { Id = series, Topic = "sensors/temp", JsonPath = "value" })]);

        var session = new SessionState { SelectedConnection = new Connection() };
        using var excludes = new TopicExcludeService(
            Substitute.For<ILogger<TopicExcludeService>>(),
            Substitute.For<IConnectionSettings>(), session);
        Func<MqttApplicationMessageReceivedEventArgs, Task>? handler = null;
        client.When(x => x.ApplicationMessageReceivedAsync += Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>())
            .Do(x => handler = x.Arg<Func<MqttApplicationMessageReceivedEventArgs, Task>>());

        using var service = new ChartDataService(client, new JsonFieldExtractor(), registry, settings,
            TestPipelineHelper.BuildBuiltInPipeline(), Substitute.For<ISparkplugSettings>(),
            topicExcludeService: excludes);
        service.SetConnection(connection);
        await service.StartAsync();
        await handler!(MakeArgs("sensors/temp", """{"value": 1}"""));
        registry.GetTopics().Should().Contain("sensors/temp");
        service.GetPoints(series).Should().NotBeEmpty();

        await service.StopAsync();
        (await excludes.Add("sensors/#").WaitAsync(TimeSpan.FromSeconds(10))).IsValid.Should().BeTrue();

        registry.GetTopics().Should().BeEmpty(
            "stopping must not blind the chart to purges, or excluded data outlives the exclusion");
        service.GetPoints(series).Should().BeEmpty();

        await service.StartAsync();
        await handler!(MakeArgs("sensors/temp", """{"value": 2}"""));

        service.GetPoints(series).Should().BeEmpty();
        registry.GetTopics().Should().BeEmpty();
    }

    [Test]
    public async Task AdmittedHandlerFinishingAfterStop_LeavesNoExcludedDataOnRestart()
    {
        var client = Substitute.For<IMqttManagedClient>();
        var registry = new ChartFieldRegistry();
        var settings = Substitute.For<IChartSettings>();
        var connection = Guid.NewGuid();
        var series = Guid.NewGuid();
        settings.GetCharts(connection).Returns([
            ConfigWith(100, new ChartSeries { Id = series, Topic = "sensors/temp", JsonPath = "value" })]);

        var session = new SessionState { SelectedConnection = new Connection() };
        using var excludes = new TopicExcludeService(
            Substitute.For<ILogger<TopicExcludeService>>(),
            Substitute.For<IConnectionSettings>(), session);
        Func<MqttApplicationMessageReceivedEventArgs, Task>? handler = null;
        client.When(x => x.ApplicationMessageReceivedAsync += Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>())
            .Do(x => handler = x.Arg<Func<MqttApplicationMessageReceivedEventArgs, Task>>());

        var extractor = new BarrierFieldExtractor();
        using var service = new ChartDataService(client, extractor, registry, settings,
            TestPipelineHelper.BuildBuiltInPipeline(), Substitute.For<ISparkplugSettings>(),
            topicExcludeService: excludes);
        service.SetConnection(connection);
        await service.StartAsync();

        var fireTask = Task.Run(() => handler!(MakeArgs("sensors/temp", """{"value": 1}""")));
        Task<TopicExcludeOperationResult>? addTask;
        bool filterApplied;
        try
        {
            (await SatisfiesAsync(() => extractor.Reached, TimeSpan.FromSeconds(5))).Should().BeTrue(
                "the handler has to be admitted inside the read gate before the service stops");
            await service.StopAsync();
            addTask = excludes.Add("sensors/#");
            filterApplied = await SatisfiesAsync(
                () => excludes.IsExcluded("sensors/temp"), TimeSpan.FromSeconds(2));
        }
        finally
        {
            extractor.Release();
        }

        var purgeResult = await addTask.WaitAsync(TimeSpan.FromSeconds(10));
        await fireTask.WaitAsync(TimeSpan.FromSeconds(10));

        filterApplied.Should().BeTrue();
        purgeResult.IsValid.Should().BeTrue();
        registry.GetTopics().Should().BeEmpty(
            "the admitted handler writes after the stop, so the purge still has to reach the chart");
        service.GetPoints(series).Should().BeEmpty();

        await service.StartAsync();
        registry.GetTopics().Should().BeEmpty();
        service.GetPoints(series).Should().BeEmpty();
    }

    [Test]
    public async Task StartStopStart_AttachesExclusionHooksOnlyOnce()
    {
        var client = Substitute.For<IMqttManagedClient>();
        var excludes = Substitute.For<ITopicExcludeService>();
        var service = new ChartDataService(client, new JsonFieldExtractor(), new ChartFieldRegistry(),
            Substitute.For<IChartSettings>(), TestPipelineHelper.BuildBuiltInPipeline(),
            Substitute.For<ISparkplugSettings>(), topicExcludeService: excludes);

        await service.StartAsync();
        await service.StopAsync();
        await service.StartAsync();
        await service.StopAsync();
        service.Dispose();

        excludes.Received(1).PurgeExcludedTopic += Arg.Any<Action<string>>();
        excludes.Received(1).TopicExcluded += Arg.Any<Action<string>>();
        excludes.Received(1).PurgeExcludedSparkplug += Arg.Any<Action<SparkplugTopicExclude>>();
        excludes.Received(1).SparkplugExcluded += Arg.Any<Action<SparkplugTopicExclude>>();
        excludes.Received(1).PurgeExcludedTopic -= Arg.Any<Action<string>>();
        excludes.Received(1).TopicExcluded -= Arg.Any<Action<string>>();
        excludes.Received(1).PurgeExcludedSparkplug -= Arg.Any<Action<SparkplugTopicExclude>>();
        excludes.Received(1).SparkplugExcluded -= Arg.Any<Action<SparkplugTopicExclude>>();
    }

    [Test]
    public async Task SparkplugAliasOnlyMetric_ResolvesNameFromDecodedPayload()
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
                Metrics = [new MetricSnapshot { Name = "Flow Rate", DataType = "double", Value = "1.0000", Alias = 42 }]
            }
        ]);

        var sparkplugSettings = Substitute.For<ISparkplugSettings>();
        sparkplugSettings.Sparkplug.Returns(new SparkplugSettings { EnrichAliasNames = true });

        var seriesId = Guid.NewGuid();
        var settings = Substitute.For<IChartSettings>();
        settings.GetCharts(Arg.Any<Guid>()).Returns(
        [
            ConfigWith(100, new ChartSeries { Id = seriesId, Topic = "spBv1.0/g/DDATA/eon1", JsonPath = "metrics.Flow Rate" })
        ]);

        var client = Substitute.For<IMqttManagedClient>();
        Func<MqttApplicationMessageReceivedEventArgs, Task>? handler = null;
        client.When(x => x.ApplicationMessageReceivedAsync += Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>())
            .Do(x => handler = x.Arg<Func<MqttApplicationMessageReceivedEventArgs, Task>>());

        using var service = new ChartDataService(client, new JsonFieldExtractor(), new ChartFieldRegistry(),
            settings, TestPipelineHelper.BuildBuiltInPipeline(), sparkplugSettings, topology);
        await service.StartAsync();

        await handler!(MakeBinaryArgs("spBv1.0/g/DDATA/eon1", AliasOnlySparkplugPayload()));

        service.GetPoints(seriesId).Should().ContainSingle()
            .Which.Value.Should().BeApproximately(24.5, 0.001);
    }

    [Test]
    public async Task SparkplugAliasOnlyMetric_PluginDecoderNullTypedPayload_StillResolvesChartPoint()
    {
        await AssertPluginTypedPayloadFallbackResolvesChartPoint(null);
    }

    [Test]
    public async Task SparkplugAliasOnlyMetric_PluginDecoderForeignTypedPayload_StillResolvesChartPoint()
    {
        await AssertPluginTypedPayloadFallbackResolvesChartPoint(new object());
    }

    private static async Task AssertPluginTypedPayloadFallbackResolvesChartPoint(object? typedPayload)
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
                Metrics = [new MetricSnapshot { Name = "Flow Rate", DataType = "double", Value = "1.0000", Alias = 42 }]
            }
        ]);

        var sparkplugSettings = Substitute.For<ISparkplugSettings>();
        sparkplugSettings.Sparkplug.Returns(new SparkplugSettings { EnrichAliasNames = true });

        var seriesId = Guid.NewGuid();
        var settings = Substitute.For<IChartSettings>();
        settings.GetCharts(Arg.Any<Guid>()).Returns(
        [
            ConfigWith(100, new ChartSeries { Id = seriesId, Topic = "spBv1.0/g/DDATA/eon1", JsonPath = "metrics.Flow Rate" })
        ]);

        var client = Substitute.For<IMqttManagedClient>();
        Func<MqttApplicationMessageReceivedEventArgs, Task>? handler = null;
        client.When(x => x.ApplicationMessageReceivedAsync += Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>())
            .Do(x => handler = x.Arg<Func<MqttApplicationMessageReceivedEventArgs, Task>>());

        var pipeline = TestPipelineHelper.BuildPipelineWithDecoderOverride(
            new TestPipelineHelper.WrappedTypedPayloadSparkplugDecoder(typedPayload));

        using var service = new ChartDataService(client, new JsonFieldExtractor(), new ChartFieldRegistry(),
            settings, pipeline, sparkplugSettings, topology);
        await service.StartAsync();

        await handler!(MakeBinaryArgs("spBv1.0/g/DDATA/eon1", AliasOnlySparkplugPayload()));

        service.GetPoints(seriesId).Should().ContainSingle()
            .Which.Value.Should().BeApproximately(24.5, 0.001);
    }

    private static MqttApplicationMessageReceivedEventArgs MakeBinaryArgs(string topic, byte[] payload)
    {
        var appMsg = new MqttApplicationMessageBuilder()
            .WithTopic(topic).WithPayload(payload).Build();
        var packet = new MQTTnet.Packets.MqttPublishPacket { Topic = topic };
        return new MqttApplicationMessageReceivedEventArgs("test-client", appMsg, packet, null);
    }

    private static byte[] AliasOnlySparkplugPayload()
    {
        var payload = new Payload { Timestamp = 2 };
        payload.Metrics.Add(new Payload.Types.Metric { Alias = 42, Datatype = 10, DoubleValue = 24.5 });
        return payload.ToByteArray();
    }

    private static async Task<bool> SatisfiesAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;

            await Task.Delay(10);
        }

        return condition();
    }

    private sealed class BarrierFieldExtractor : IJsonFieldExtractor
    {
        private readonly JsonFieldExtractor _inner = new();
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reached;

        public bool Reached => Volatile.Read(ref _reached) != 0;

        public void Release() => _release.TrySetResult();

        public IReadOnlyDictionary<string, ExtractedField> Extract(string jsonPayload) =>
            ExtractCore(jsonPayload);

        public IReadOnlyDictionary<string, ExtractedField> Extract(
            string jsonPayload,
            IReadOnlyDictionary<ulong, string>? aliasNames) =>
            ExtractCore(jsonPayload);

        private IReadOnlyDictionary<string, ExtractedField> ExtractCore(string jsonPayload)
        {
            Interlocked.Exchange(ref _reached, 1);
            _release.Task.Wait(TimeSpan.FromSeconds(30));
            return _inner.Extract(jsonPayload);
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
