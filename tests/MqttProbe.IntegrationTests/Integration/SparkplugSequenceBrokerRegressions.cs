using MQTTnet;
using MQTTnet.Protocol;
using MqttProbe.Core.Models.Sparkplug;
using MqttProbe.TestInfrastructure.Fixtures;

namespace MqttProbe.IntegrationTests.Integration;

public sealed partial class SparkplugSequenceRecoveryTests
{
    [Test]
    public async Task AutomaticReconnectOnSameAttempt_RestoresCoverageAndUsesFreshBirthBaseline()
    {
        await using var context = await SequenceContext.CreateAsync(
            _broker!, "automatic-reconnect", autoReconnectDelay: TimeSpan.FromSeconds(1), startWithAttempt: true);
        await context.WaitForInitialConnectionAsync();
        context.InitialConnectionEpoch.Should().NotBeNull();
        context.InitialAttemptId.Should().NotBeNull();
        await context.PublishBirthAsync(1);
        await context.WaitForStateAsync(SpbSequenceState.InOrder);

        await context.TakeOverManagedConnectionAsync();
        await context.WaitForReconnectAsync();

        context.ConnectedSnapshots.Should().HaveCount(2);
        var first = context.ConnectedSnapshots.First();
        var reconnected = context.ConnectedSnapshots.Last();
        reconnected.Epoch.Should().NotBe(first.Epoch, "the reconnect must establish a new physical connection epoch");
        reconnected.Snapshot.Should().NotBeNull();
        reconnected.Snapshot!.ConfirmedFilters.Should().Contain($"spBv1.0/{context.GroupId}/NBIRTH/{context.NodeId}");
        reconnected.Snapshot.ConfirmedFilters.Should().Contain($"spBv1.0/{context.GroupId}/NDATA/{context.NodeId}");
        reconnected.Snapshot.ConfirmedFilters.Should().Contain($"spBv1.0/{context.GroupId}/DDATA/{context.NodeId}/+");
        context.ConnectOutcomes.Should().ContainSingle(outcome => outcome.AttemptId == context.InitialAttemptId);

        var staleEpochPublished = await context.Client.TryPublishConnectedAsync(
            new MqttApplicationMessageBuilder().WithTopic($"spBv1.0/{context.GroupId}/NDATA/{context.NodeId}").Build(),
            context.InitialConnectionEpoch);
        staleEpochPublished.Should().BeFalse("the retired connection epoch must not publish through the live reconnect");
        context.NodeRebirthReceived.Task.IsCompleted.Should().BeFalse("a stale publish must not cause an NCMD");
        context.NodeRebirthCount.Should().Be(0);

        await context.PublishBirthAsync(20);
        await context.WaitForStateAsync(SpbSequenceState.InOrder);
        await context.PublishDataAsync(22);
        await context.WaitForStateAsync(SpbSequenceState.GapDetected);
        (await context.NodeRebirthReceived.Task.WaitAsync(TimeSpan.FromSeconds(8)))
            .Should().Be($"spBv1.0/{context.GroupId}/NCMD/{context.NodeId}");
    }

    [Test]
    public async Task RejectedRequiredDeviceSubscription_DoesNotQualifyForSequenceRecovery()
    {
        const string groupId = "sequence-rejected-suback";
        const string deniedFilter = $"spBv1.0/{groupId}/DDATA/node/+";
        await using var broker = await MtlsBrokerFixture.StartAsync(deniedFilter);
        await using var context = await SequenceContext.CreateAsync(
            broker, "rejected-suback", fixedGroupId: groupId, suppressPostStartSubscribe: true);
        await context.WaitForInitialConnectionAsync();

        var rawReason = await context.SubscribeWithRawClientAsync(deniedFilter);
        rawReason.Should().Be(MqttClientSubscribeResultCode.NotAuthorized,
            "the broker must reject the filter with MQTT 5 Not Authorized (0x87), not merely suppress delivery");
        ((byte)rawReason).Should().Be(0x87);

        var deniedDeviceFilter = new MqttTopicFilterBuilder().WithTopic(deniedFilter)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build();
        var managedSubscribe = () => context.Client.SubscribeAsync([deniedDeviceFilter]);
        await managedSubscribe.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("The broker rejected or did not acknowledge 1 subscription(s).");

        var snapshot = context.Client.LiveSubscriptionSnapshot;
        snapshot.Should().NotBeNull();
        snapshot!.ConfirmedFilters.Should().NotContain(deniedFilter);
        snapshot.ConfirmedFilters.Should().Contain($"spBv1.0/{groupId}/NBIRTH/node");
        snapshot.ConfirmedFilters.Should().Contain($"spBv1.0/{groupId}/NDATA/node");

        await context.PublishBirthAsync(1);
        await context.WaitForStateAsync(SpbSequenceState.InOrder);
        await context.PublishDataAsync(3);
        await context.WaitForStateAsync(SpbSequenceState.GapDetected);

        var rebirthWasUnexpectedlyPublished = false;
        try
        {
            await context.NodeRebirthReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));
            rebirthWasUnexpectedlyPublished = true;
        }
        catch (TimeoutException)
        {
        }
        rebirthWasUnexpectedlyPublished.Should().BeFalse(
            "partial broker-confirmed subscription coverage must not trigger an automatic NCMD");
        context.NodeRebirthCount.Should().Be(0);
    }
}
