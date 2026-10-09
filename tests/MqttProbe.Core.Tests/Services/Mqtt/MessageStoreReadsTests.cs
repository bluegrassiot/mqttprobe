using System.Collections.Immutable;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.TestInfrastructure;

namespace MqttProbe.Core.Tests.Services.Mqtt;

[TestFixture]
public sealed class MessageStoreReadsTests
{
    [Test]
    public async Task MissingTopic_DoesNotReadExistingSelection()
    {
        var manager = Substitute.For<IMessageStoreManager>();
        var currentSelection = State("existing/topic");
        manager.GetSelectedTopicState().Returns(currentSelection);

        var result = await MessageStoreReads.ReadMessagesAsync(manager, "missing/topic", 10);

        Assert.That(result, Is.Empty);
        manager.Received(1).SelectTopic("missing/topic");
        await manager.DidNotReceive().GetSelectedMessagesAsync(Arg.Any<SelectedTopicToken>(), Arg.Any<int>());
    }

    [Test]
    public async Task DifferentExistingTopic_ReadsWithNewSelectionToken()
    {
        var manager = Substitute.For<IMessageStoreManager>();
        var previousSelection = State("previous/topic");
        var newSelection = State("new/topic");
        var message = new MqttMessage("payload", "new/topic", false,
            MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce);
        var selectedState = previousSelection;
        manager.GetSelectedTopicState().Returns(_ => selectedState);
        manager.When(m => m.SelectTopic("new/topic")).Do(_ => selectedState = newSelection);
        manager.GetSelectedMessagesAsync(newSelection.Token, 10)
            .Returns(Task.FromResult<SelectedMessagesSnapshot?>(Snapshot(newSelection, message)));

        var result = await MessageStoreReads.ReadMessagesAsync(manager, "new/topic", 10);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0], Is.SameAs(message));
        manager.Received(1).SelectTopic("new/topic");
        _ = manager.Received(1).GetSelectedMessagesAsync(newSelection.Token, 10);
        _ = manager.DidNotReceive().GetSelectedMessagesAsync(previousSelection.Token, 10);
    }

    [Test]
    public async Task NullSnapshot_ThrowsInsteadOfReturningEmptyMessages()
    {
        var manager = Substitute.For<IMessageStoreManager>();
        var selection = State("topic");
        manager.GetSelectedTopicState().Returns(selection);
        manager.GetSelectedMessagesAsync(selection.Token, 10)
            .Returns(Task.FromResult<SelectedMessagesSnapshot?>(null));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await MessageStoreReads.ReadMessagesAsync(manager, "topic", 10));

        Assert.That(exception!.Message, Does.Contain("stale or unavailable"));
    }

    [Test]
    public async Task SuccessfulEmptySnapshot_ReturnsEmptyMessages()
    {
        var manager = Substitute.For<IMessageStoreManager>();
        var selection = State("topic");
        manager.GetSelectedTopicState().Returns(selection);
        manager.GetSelectedMessagesAsync(selection.Token, 10)
            .Returns(Task.FromResult<SelectedMessagesSnapshot?>(Snapshot(selection)));

        var result = await MessageStoreReads.ReadMessagesAsync(manager, "topic", 10);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task SlashNormalizedTopic_ReadsSelectedMessages()
    {
        var manager = Substitute.For<IMessageStoreManager>();
        var selection = State("a/b");
        var message = new MqttMessage("payload", "a/b", false,
            MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce);
        manager.GetSelectedTopicState().Returns(selection);
        manager.GetSelectedMessagesAsync(selection.Token, 10)
            .Returns(Task.FromResult<SelectedMessagesSnapshot?>(Snapshot(selection, message)));

        var result = await MessageStoreReads.ReadMessagesAsync(manager, "//a///b//", 10);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0], Is.SameAs(message));
        _ = manager.Received(1).GetSelectedMessagesAsync(selection.Token, 10);
    }

    private static SelectedTopicState State(string topic) =>
        new(new SelectedTopicToken(Guid.NewGuid(), 1, 1), topic, 1);

    private static SelectedMessagesSnapshot Snapshot(SelectedTopicState state, params MqttMessage[] messages) =>
        new(state, 10, messages.ToImmutableArray());
}
