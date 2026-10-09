using Microsoft.Extensions.Logging;
using MQTTnet;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Metrics;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Tests.Utilities;

namespace MqttProbe.Core.Tests.Services.Mqtt;

public partial class MessageStoreManagerMessageHandlerTests
{
    [TestCase("sensors/temperature", "sensors/temperature", "sensors/humidity")]
    [TestCase("sensors/+/reading", "sensors/temperature/reading", "sensors/humidity/status")]
    public async Task TopicExclusion_RemovalAllowsFreshSelectedParentDataWithoutRestoringPurgedHistory(
        string exclusion,
        string matchingTopic,
        string siblingTopic)
    {
        var client = Substitute.For<IMqttManagedClient>();
        Func<MqttApplicationMessageReceivedEventArgs, Task>? handler = null;
        client.When(x => x.ApplicationMessageReceivedAsync += Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>())
            .Do(x => handler = x.Arg<Func<MqttApplicationMessageReceivedEventArgs, Task>>());

        var session = new SessionState { SelectedConnection = new Connection() };
        var excludes = new TopicExcludeService(
            Substitute.For<ILogger<TopicExcludeService>>(),
            Substitute.For<IConnectionSettings>(), session);
        var performance = Substitute.For<IPerformanceSettings>();
        performance.Performance.Returns(new AppConfiguration().Performance);
        var sparkplug = Substitute.For<ISparkplugSettings>();
        sparkplug.Sparkplug.Returns(new SparkplugSettings { EnrichAliasNames = true });

        using var manager = new MessageStoreManager(client, Substitute.For<ILogger<MessageStoreManager>>(),
            performance, Substitute.For<IUxMetricsService>(), TestPipelineHelper.BuildBuiltInPipeline(), sparkplug,
            topicExcludeService: excludes);
        await manager.Start();

        await handler!(MakeArgs(matchingTopic, "before-exclusion"));
        await handler!(MakeArgs(siblingTopic, "sibling-history"));

        manager.SelectTopic("sensors");
        var initialSelection = manager.GetSelectedTopicState();
        initialSelection.MessageCount.Should().Be(2);
        var initialMessages = (await manager.GetSelectedMessagesAsync(initialSelection.Token, 10))!.Messages;
        initialMessages.Should().Contain(message => message.Payload == "before-exclusion");
        initialMessages.Should().Contain(message => message.Payload == "sibling-history");

        (await excludes.Add(exclusion)).IsValid.Should().BeTrue();
        var purgedSelection = manager.GetSelectedTopicState();
        purgedSelection.Token.Should().NotBe(initialSelection.Token);
        purgedSelection.MessageCount.Should().Be(1);
        var afterPurge = (await manager.GetSelectedMessagesAsync(purgedSelection.Token, 10))!.Messages;
        afterPurge.Should().ContainSingle(message => message.Payload == "sibling-history");
        afterPurge.Should().NotContain(message => message.Payload == "before-exclusion");

        await handler!(MakeArgs(matchingTopic, "blocked-during-exclusion"));
        var blockedSelection = manager.GetSelectedTopicState();
        blockedSelection.MessageCount.Should().Be(1);
        var afterBlocked = (await manager.GetSelectedMessagesAsync(blockedSelection.Token, 10))!.Messages;
        afterBlocked.Should().ContainSingle(message => message.Payload == "sibling-history");
        afterBlocked.Should().NotContain(message => message.Payload == "blocked-during-exclusion");

        (await excludes.Remove([exclusion])).IsValid.Should().BeTrue();
        await handler!(MakeArgs(matchingTopic, "fresh-after-removal"));

        var refreshedSelection = manager.GetSelectedTopicState();
        refreshedSelection.MessageCount.Should().Be(2);
        refreshedSelection.Token.Should().NotBe(blockedSelection.Token);
        var refreshedMessages = (await manager.GetSelectedMessagesAsync(refreshedSelection.Token, 10))!.Messages;
        refreshedMessages.Should().Contain(message => message.Payload == "fresh-after-removal");
        refreshedMessages.Should().Contain(message => message.Payload == "sibling-history");
        refreshedMessages.Should().NotContain(message => message.Payload == "before-exclusion");
        refreshedMessages.Should().NotContain(message => message.Payload == "blocked-during-exclusion");

        excludes.Dispose();
    }
}
