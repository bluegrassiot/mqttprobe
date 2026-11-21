using Microsoft.Extensions.Logging;
using MQTTnet;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Metrics;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Core.Services.Sparkplug;
using MqttProbe.Tests.Utilities;

namespace MqttProbe.Core.Tests.Services.Mqtt;

public partial class MessageStoreManagerMessageHandlerTests
{
    [Test]
    public async Task MessageHandler_NamedMetricWithoutTopology_IsCapturedOnTheMessage()
    {
        MqttMessage? received = null;
        _manager.MessageReceived += message =>
        {
            received = message;
            return Task.CompletedTask;
        };

        await _capturedHandler!(MakeBinaryArgs("spBv1.0/g/NDATA/n1", SparkplugPayload()));

        received.Should().NotBeNull();
        received.SparkplugMetricNames.Should().Equal("temperature");
    }

    [Test]
    public async Task MessageHandler_MetricNamesAreCapturedWhenDisplayEnrichmentIsDisabled()
    {
        var sparkplug = Substitute.For<ISparkplugSettings>();
        sparkplug.Sparkplug.Returns(new SparkplugSettings { EnrichAliasNames = false });
        var topology = new SparkplugTopologyService(sparkplugSettings: sparkplug);
        var built = BuildManagerWithSparkplugSettings(sparkplug, topology);
        using var manager = built.Manager;

        MqttMessage? received = null;
        manager.MessageReceived += message =>
        {
            received = message;
            return Task.CompletedTask;
        };

        await built.Fire(MakeBinaryArgs("spBv1.0/g/NBIRTH/n1", SparkplugPayload()));
        await built.Fire(MakeBinaryArgs("spBv1.0/g/NDATA/n1", AliasOnlyDataPayload()));

        received.Should().NotBeNull();
        received.AliasNames.Should().BeNull("display enrichment is off");
        received.SparkplugMetricNames.Should().Equal("temperature");
    }

    private static (MessageStoreManager Manager, Func<MqttApplicationMessageReceivedEventArgs, Task> Fire)
        BuildManagerWithSparkplugSettings(ISparkplugSettings sparkplug, ISparkplugTopologyService topology)
    {
        var client = Substitute.For<IMqttManagedClient>();
        SetCompleteSubscriptionContext(client);
        Func<MqttApplicationMessageReceivedEventArgs, Task>? handler = null;
        client.When(x => x.ApplicationMessageReceivedAsync += Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>())
            .Do(x => handler = x.Arg<Func<MqttApplicationMessageReceivedEventArgs, Task>>());

        var performance = Substitute.For<IPerformanceSettings>();
        performance.Performance.Returns(new AppConfiguration().Performance);

        var manager = new MessageStoreManager(client, Substitute.For<ILogger<MessageStoreManager>>(),
            performance, Substitute.For<IUxMetricsService>(), TestPipelineHelper.BuildBuiltInPipeline(),
            sparkplug, topology);
        manager.Start().GetAwaiter().GetResult();
        return (manager, handler!);
    }
}
