using Microsoft.Extensions.Logging;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Mqtt;

namespace MqttProbe.Core.Tests.Services.Mqtt;

[TestFixture]
public sealed class TopicTreeStoreSelectionClearTests
{
    [Test]
    public void MatchingState_ClearsOnceThenRejects()
    {
        var store = CreateStore();
        store.Add("root/tip", Message("first", 1, "root/tip"));
        store.SelectTopic("root/tip");

        var version = store.Version;
        var expected = store.GetSelectedTopicState();

        store.TryClearSelection(version, expected).Should().BeTrue();

        var cleared = store.GetSelectedTopicState();
        cleared.FullTopic.Should().BeNull();
        cleared.MessageCount.Should().Be(0);
        cleared.Token.Generation.Should().Be(expected.Token.Generation + 1);
        cleared.Token.ContentVersion.Should().Be(expected.Token.ContentVersion + 1);

        store.TryClearSelection(version, expected).Should().BeFalse("a second clear has nothing left to drop");
        store.GetSelectedTopicState().Should().Be(cleared);
    }

    [Test]
    public void MatchingState_AdvancesSelectionOnceWithoutTouchingTree()
    {
        var store = CreateStore();
        store.Add("root/tip", Message("first", 1, "root/tip"));
        store.Add("other", Message("other", 2, "other"));
        store.SelectTopic("root/tip");

        var version = store.Version;
        var treeBefore = store.GetTopicTreeSnapshot();
        var nodes = store.TopicNodeCount;
        var messages = store.TotalStoredMessages;
        var expected = store.GetSelectedTopicState();

        store.TryClearSelection(version, expected).Should().BeTrue();

        store.Version.Should().Be(version);
        store.TopicNodeCount.Should().Be(nodes);
        store.TotalStoredMessages.Should().Be(messages);
        store.GetTopicTreeSnapshot().Should().BeSameAs(treeBefore);
        store.GetSelectedTopicState().Token.Generation.Should().Be(expected.Token.Generation + 1);
        store.GetSelectedTopicState().Token.ContentVersion.Should().Be(expected.Token.ContentVersion + 1);
    }

    [Test]
    public void StaleTreeVersionFromSelectedTopicAdd_Rejects()
    {
        var store = CreateStore();
        store.Add("root/tip", Message("first", 1, "root/tip"));
        store.SelectTopic("root/tip");

        var staleVersion = store.Version;
        var expected = store.GetSelectedTopicState();

        store.Add("root/tip", Message("second", 2, "root/tip"));
        var before = store.GetSelectedTopicState();

        store.TryClearSelection(staleVersion, expected).Should().BeFalse("the tree moved on since capture");
        store.GetSelectedTopicState().Should().Be(before);
        store.TotalStoredMessages.Should().Be(2);
    }

    [Test]
    public void StaleTreeVersionFromUnrelatedAdd_Rejects()
    {
        var store = CreateStore();
        store.Add("root/tip", Message("first", 1, "root/tip"));
        store.SelectTopic("root/tip");

        var staleVersion = store.Version;
        var expected = store.GetSelectedTopicState();

        store.Add("root/other", Message("other", 2, "root/other"));
        var before = store.GetSelectedTopicState();

        before.Token.Generation.Should().Be(expected.Token.Generation);
        before.Token.ContentVersion.Should().Be(expected.Token.ContentVersion);
        before.FullTopic.Should().Be(expected.FullTopic);

        store.TryClearSelection(staleVersion, expected).Should().BeFalse("only the tree version differs");
        store.GetSelectedTopicState().Should().Be(before);
    }

    [Test]
    public void GenerationMovedAwayAndBack_Rejects()
    {
        var store = CreateStore();
        store.Add("root/tip", Message("first", 1, "root/tip"));
        store.Add("root/other", Message("other", 2, "root/other"));
        store.SelectTopic("root/tip");

        var expected = store.GetSelectedTopicState();

        store.SelectTopic("root/other");
        store.SelectTopic("root/tip");

        var before = store.GetSelectedTopicState();
        before.FullTopic.Should().Be("root/tip", "the path came back");
        before.Token.Generation.Should().NotBe(expected.Token.Generation, "the detour advanced the generation");

        store.TryClearSelection(store.Version, expected).Should().BeFalse("the captured generation is stale");
        store.GetSelectedTopicState().Should().Be(before);
    }

    [Test]
    public void WrongPathWithMatchingToken_Rejects()
    {
        var store = CreateStore();
        store.Add("root/tip", Message("first", 1, "root/tip"));
        store.Add("root/other", Message("other", 2, "root/other"));
        store.SelectTopic("root/tip");

        var version = store.Version;
        var current = store.GetSelectedTopicState();
        var wrongPath = current with { FullTopic = "root/other" };

        store.TryClearSelection(version, wrongPath).Should().BeFalse("the expected path is not the selected one");
        store.GetSelectedTopicState().Should().Be(current);
    }

    [Test]
    public void ForeignStoreToken_Rejects()
    {
        var store = CreateStore();
        store.Add("root/tip", Message("first", 1, "root/tip"));
        store.SelectTopic("root/tip");

        var version = store.Version;
        var current = store.GetSelectedTopicState();

        var foreignStore = CreateStore();
        foreignStore.Add("root/tip", Message("first", 1, "root/tip"));
        foreignStore.SelectTopic("root/tip");
        var foreign = foreignStore.GetSelectedTopicState();

        foreign.FullTopic.Should().Be(current.FullTopic);
        foreign.Token.Generation.Should().Be(current.Token.Generation);
        foreign.Token.StoreId.Should().NotBe(current.Token.StoreId);

        store.TryClearSelection(version, foreign).Should().BeFalse("the token was minted by another store");
        store.GetSelectedTopicState().Should().Be(current);
    }

    [Test]
    public void NoSelection_Rejects()
    {
        var store = CreateStore();
        store.Add("root/tip", Message("first", 1, "root/tip"));

        var state = store.GetSelectedTopicState();
        state.FullTopic.Should().BeNull();

        store.TryClearSelection(store.Version, state).Should().BeFalse("there is no selection to clear");
        store.GetSelectedTopicState().Should().Be(state);
    }

    private static TopicTreeStore CreateStore(int maxMessages = 100, int maxTopicNodes = 100)
    {
        var configuration = new AppConfiguration
        {
            Performance = new PerformanceSettings
            {
                MaxStoredMessages = maxMessages,
                MaxTopicNodes = maxTopicNodes
            }
        };
        var settings = Substitute.For<IPerformanceSettings>();
        settings.Performance.Returns(configuration.Performance);
        return new TopicTreeStore(settings, Substitute.For<ILogger>());
    }

    private static MqttMessage Message(string payload, int second, string topic) =>
        new(payload, topic, false, MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce)
        {
            DateTimeReceived = DateTime.UnixEpoch.AddSeconds(second)
        };
}
