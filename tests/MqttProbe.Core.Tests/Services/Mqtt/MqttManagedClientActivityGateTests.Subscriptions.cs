using MQTTnet;
using MQTTnet.Packets;
using MqttProbe.Core.Services.Mqtt;

namespace MqttProbe.Core.Tests.Services.Mqtt;

public partial class MqttManagedClientActivityGateTests
{
    [Test]
    public async Task Resubscribe_skipped_on_connect_after_revoke()
    {
        var client = Substitute.For<IMqttClient>();
        client.IsConnected.Returns(false);
        var gate = new RevocableSessionActivityGate();
        await using var sut = new MqttManagedClient(client, gate: gate);
        await sut.StartAsync(BuildOptions());

        await sut.SubscribeAsync([Filter("a/b")]);
        client.ClearReceivedCalls();
        gate.Revoke();

        client.IsConnected.Returns(true);
        client.ConnectedAsync += Raise.Event<Func<MqttClientConnectedEventArgs, Task>>(ConnectedArgs());

        await client.DidNotReceive().SubscribeAsync(Arg.Any<MqttClientSubscribeOptions>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SubscribeAsync_passes_linked_token_to_client()
    {
        var client = Substitute.For<IMqttClient>();
        client.IsConnected.Returns(true);
        var gate = new RevocableSessionActivityGate();
        await using var sut = new MqttManagedClient(client, gate: gate);
        await sut.StartAsync(BuildOptions());
        await RaiseConnectedAsync(client);
        CancellationToken capturedToken = default;
        var tcs = new TaskCompletionSource<MqttClientSubscribeResult>();
        client.SubscribeAsync(Arg.Any<MqttClientSubscribeOptions>(), Arg.Do<CancellationToken>(t => capturedToken = t))
            .Returns(tcs.Task);

        var subscribeTask = sut.SubscribeAsync([Filter("a/b")]);

        capturedToken.CanBeCanceled.Should().BeTrue();
        gate.Revoke();
        await gate.RevocationCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        capturedToken.IsCancellationRequested.Should().BeTrue();
        tcs.SetResult(SubscribeResult(Filter("a/b")));
        await subscribeTask;
    }

    [Test]
    public async Task UnsubscribeAsync_passes_linked_token_to_client()
    {
        var client = Substitute.For<IMqttClient>();
        client.IsConnected.Returns(true);
        var gate = new RevocableSessionActivityGate();
        await using var sut = new MqttManagedClient(client, gate: gate);
        await sut.StartAsync(BuildOptions());
        await RaiseConnectedAsync(client);
        client.SubscribeAsync(Arg.Any<MqttClientSubscribeOptions>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(SubscribeResult(call.Arg<MqttClientSubscribeOptions>().TopicFilters.ToArray())));
        await sut.SubscribeAsync([Filter("a/b")]);
        CancellationToken capturedToken = default;
        var tcs = new TaskCompletionSource<MqttClientUnsubscribeResult>();
        client.UnsubscribeAsync(Arg.Any<MqttClientUnsubscribeOptions>(), Arg.Do<CancellationToken>(t => capturedToken = t))
            .Returns(tcs.Task);

        var unsubscribeTask = sut.UnsubscribeAsync(["a/b"]);

        capturedToken.CanBeCanceled.Should().BeTrue();
        gate.Revoke();
        await gate.RevocationCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        capturedToken.IsCancellationRequested.Should().BeTrue();
        tcs.SetResult(UnsubscribeResult("a/b"));
        await unsubscribeTask;
    }

    [Test]
    public async Task Resubscribe_passes_revocation_token_to_client()
    {
        var client = Substitute.For<IMqttClient>();
        client.IsConnected.Returns(false);
        var gate = new RevocableSessionActivityGate();
        Func<MqttClientConnectedEventArgs, Task>? connectedHandler = null;
        client.When(c => c.ConnectedAsync += Arg.Any<Func<MqttClientConnectedEventArgs, Task>>())
            .Do(call => connectedHandler = call.Arg<Func<MqttClientConnectedEventArgs, Task>>());
        await using var sut = new MqttManagedClient(client, gate: gate);
        await sut.StartAsync(BuildOptions());

        await sut.SubscribeAsync([Filter("a/b")]);
        var subscribeStarted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tcs = new TaskCompletionSource<MqttClientSubscribeResult>();
        client.SubscribeAsync(Arg.Any<MqttClientSubscribeOptions>(), Arg.Do<CancellationToken>(token =>
            subscribeStarted.TrySetResult(token)))
            .Returns(tcs.Task);
        client.ClearReceivedCalls();

        client.IsConnected.Returns(true);
        var connectedTask = (connectedHandler ?? throw new InvalidOperationException("Connected callback was not registered."))(ConnectedArgs());
        var capturedToken = await subscribeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        capturedToken.CanBeCanceled.Should().BeTrue();
        gate.Revoke();
        await gate.RevocationCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        capturedToken.IsCancellationRequested.Should().BeTrue();
        tcs.SetResult(SubscribeResult(Filter("a/b")));
        await connectedTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static Task RaiseConnectedAsync(IMqttClient client)
    {
        client.ConnectedAsync += Raise.Event<Func<MqttClientConnectedEventArgs, Task>>(ConnectedArgs());
        return Task.CompletedTask;
    }

    private static MqttClientSubscribeResult SubscribeResult(params MqttTopicFilter[] filters) => new(
        1,
        filters.Select(filter => new MqttClientSubscribeResultItem(
            filter, MqttClientSubscribeResultCode.GrantedQoS0)).ToArray(),
        null!,
        []);

    private static MqttClientUnsubscribeResult UnsubscribeResult(params string[] topics) => new(
        1,
        topics.Select(topic => new MqttClientUnsubscribeResultItem(
            topic, MqttClientUnsubscribeResultCode.Success)).ToArray(),
        null!,
        []);
}
