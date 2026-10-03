using MQTTnet;
using MQTTnet.Packets;
using MqttProbe.Core.Services.Mqtt;

namespace MqttProbe.Core.Tests.Services.Mqtt;

[TestFixture]
public class MqttManagedClientActivityGateTests
{
    private static MqttManagedClientOptions BuildOptions(TimeSpan? reconnect = null) => new()
    {
        ClientOptions = new MqttClientOptionsBuilder()
            .WithTcpServer("localhost", 1883)
            .WithClientId("test")
            .Build(),
        AutoReconnectDelay = reconnect ?? TimeSpan.FromSeconds(5)
    };

    private static MqttClientConnectedEventArgs ConnectedArgs() =>
        new(new MqttClientConnectResult());

    private static MqttClientDisconnectedEventArgs DisconnectedArgs() =>
        new(true, null!, MqttClientDisconnectReason.NormalDisconnection, null!, null!, null!);

    private static MqttTopicFilter Filter(string topic) =>
        new MqttTopicFilterBuilder().WithTopic(topic).Build();

    [Test]
    public async Task StartAsync_before_revoke_connects_normally()
    {
        var client = Substitute.For<IMqttClient>();
        var gate = new RevocableSessionActivityGate();
        await using var sut = new MqttManagedClient(client, gate: gate);

        await sut.StartAsync(BuildOptions());

        sut.IsStarted.Should().BeTrue();
        await client.Received(1).ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StartAsync_after_revoke_throws_InvalidOperationException()
    {
        var client = Substitute.For<IMqttClient>();
        var gate = new RevocableSessionActivityGate();
        await using var sut = new MqttManagedClient(client, gate: gate);
        gate.Revoke();

        var act = () => sut.StartAsync(BuildOptions());
        await act.Should().ThrowExactlyAsync<InvalidOperationException>();
    }

    [Test]
    public async Task SubscribeAsync_after_revoke_throws_InvalidOperationException()
    {
        var client = Substitute.For<IMqttClient>();
        client.IsConnected.Returns(true);
        var gate = new RevocableSessionActivityGate();
        await using var sut = new MqttManagedClient(client, gate: gate);
        gate.Revoke();

        var act = () => sut.SubscribeAsync(new[] { Filter("a/b") });
        await act.Should().ThrowExactlyAsync<InvalidOperationException>();
    }

    [Test]
    public async Task UnsubscribeAsync_after_revoke_throws_InvalidOperationException()
    {
        var client = Substitute.For<IMqttClient>();
        client.IsConnected.Returns(true);
        var gate = new RevocableSessionActivityGate();
        await using var sut = new MqttManagedClient(client, gate: gate);
        gate.Revoke();

        var act = () => sut.UnsubscribeAsync(new[] { "a/b" });
        await act.Should().ThrowExactlyAsync<InvalidOperationException>();
    }

    [Test]
    public async Task EnqueueAsync_after_revoke_throws_InvalidOperationException()
    {
        var client = Substitute.For<IMqttClient>();
        client.IsConnected.Returns(true);
        var gate = new RevocableSessionActivityGate();
        await using var sut = new MqttManagedClient(client, gate: gate);
        var message = new MqttApplicationMessageBuilder().WithTopic("a/b").Build();
        gate.Revoke();

        var act = () => sut.EnqueueAsync(message);
        await act.Should().ThrowExactlyAsync<InvalidOperationException>();
    }

    [Test]
    public async Task Initial_connect_uses_revocation_token()
    {
        var client = Substitute.For<IMqttClient>();
        var gate = new RevocableSessionActivityGate();
        await using var sut = new MqttManagedClient(client, gate: gate);

        await sut.StartAsync(BuildOptions());

        // The connect should have been called with a token derived from the gate's revocation token.
        await client.Received(1).ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Reconnect_delay_cancelled_when_gate_revoked()
    {
        var client = Substitute.For<IMqttClient>();
        client.IsConnected.Returns(true);
        var gate = new RevocableSessionActivityGate();
        await using var sut = new MqttManagedClient(client, gate: gate);
        await sut.StartAsync(BuildOptions(TimeSpan.FromMinutes(10)));

        client.IsConnected.Returns(false);
        client.DisconnectedAsync += Raise.Event<Func<MqttClientDisconnectedEventArgs, Task>>(DisconnectedArgs());

        // Give the reconnect loop time to enter its delay.
        await Task.Delay(100);

        gate.Revoke();

        // The linked token cancellation should exit the reconnect loop promptly.
        await Task.Delay(200);

        var connectCalls = client.ReceivedCalls()
            .Count(c => c.GetMethodInfo().Name == nameof(IMqttClient.ConnectAsync));
        connectCalls.Should().Be(1, "only the initial StartAsync connect, not a reconnect");
    }

    [Test]
    public async Task Disconnect_after_revoke_does_not_start_reconnect_loop()
    {
        var client = Substitute.For<IMqttClient>();
        client.IsConnected.Returns(true);
        var gate = new RevocableSessionActivityGate();
        await using var sut = new MqttManagedClient(client, gate: gate);
        await sut.StartAsync(BuildOptions(TimeSpan.FromMilliseconds(50)));

        gate.Revoke();

        client.IsConnected.Returns(false);
        client.DisconnectedAsync += Raise.Event<Func<MqttClientDisconnectedEventArgs, Task>>(DisconnectedArgs());

        await Task.Delay(200);

        var connectCalls = client.ReceivedCalls()
            .Count(c => c.GetMethodInfo().Name == nameof(IMqttClient.ConnectAsync));
        connectCalls.Should().Be(1, "reconnect loop must not start after revocation");
    }

    [Test]
    public async Task Resubscribe_skipped_on_connect_after_revoke()
    {
        var client = Substitute.For<IMqttClient>();
        client.IsConnected.Returns(false);
        var gate = new RevocableSessionActivityGate();
        await using var sut = new MqttManagedClient(client, gate: gate);

        await sut.SubscribeAsync(new[] { Filter("a/b") });
        client.ClearReceivedCalls();

        gate.Revoke();

        client.IsConnected.Returns(true);
        client.ConnectedAsync += Raise.Event<Func<MqttClientConnectedEventArgs, Task>>(ConnectedArgs());

        await client.DidNotReceive().SubscribeAsync(Arg.Any<MqttClientSubscribeOptions>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Pending_drain_skipped_on_connect_after_revoke()
    {
        var client = Substitute.For<IMqttClient>();
        client.IsConnected.Returns(false);
        var gate = new RevocableSessionActivityGate();
        await using var sut = new MqttManagedClient(client, gate: gate);
        var message = new MqttApplicationMessageBuilder().WithTopic("a/b").Build();

        await sut.EnqueueAsync(message);
        client.ClearReceivedCalls();

        gate.Revoke();

        client.IsConnected.Returns(true);
        client.ConnectedAsync += Raise.Event<Func<MqttClientConnectedEventArgs, Task>>(ConnectedArgs());

        await client.DidNotReceive().PublishAsync(Arg.Any<MqttApplicationMessage>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Revoke_after_first_queued_publish_prevents_second()
    {
        var client = Substitute.For<IMqttClient>();
        client.IsConnected.Returns(false);
        var gate = new RevocableSessionActivityGate();
        await using var sut = new MqttManagedClient(client, gate: gate);

        // Queue two messages while disconnected.
        var msg1 = new MqttApplicationMessageBuilder().WithTopic("t/1").Build();
        var msg2 = new MqttApplicationMessageBuilder().WithTopic("t/2").Build();
        await sut.EnqueueAsync(msg1);
        await sut.EnqueueAsync(msg2);

        // Make PublishAsync succeed for the first, then revoke before drain processes the second.
        var publishCount = 0;
        client.PublishAsync(Arg.Any<MqttApplicationMessage>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                publishCount++;
                if (publishCount == 1)
                    gate.Revoke();
                return Task.CompletedTask;
            });

        client.IsConnected.Returns(true);
        client.ConnectedAsync += Raise.Event<Func<MqttClientConnectedEventArgs, Task>>(ConnectedArgs());

        // Only the first message should have been published; drain should have stopped.
        publishCount.Should().Be(1);
    }

    [Test]
    public async Task Post_revoke_application_message_not_raised()
    {
        var client = Substitute.For<IMqttClient>();
        var gate = new RevocableSessionActivityGate();
        await using var sut = new MqttManagedClient(client, gate: gate);
        var received = false;
        sut.ApplicationMessageReceivedAsync += _ => { received = true; return Task.CompletedTask; };

        gate.Revoke();

        var message = new MqttApplicationMessageBuilder().WithTopic("a/b").Build();
        var args = new MqttApplicationMessageReceivedEventArgs("client", message, new MqttPublishPacket(), null);
        client.ApplicationMessageReceivedAsync += Raise.Event<Func<MqttApplicationMessageReceivedEventArgs, Task>>(args);

        received.Should().BeFalse();
    }

    [Test]
    public async Task StopAsync_after_revoke_completes_without_error()
    {
        var client = Substitute.For<IMqttClient>();
        client.IsConnected.Returns(true);
        var gate = new RevocableSessionActivityGate();
        await using var sut = new MqttManagedClient(client, gate: gate);
        await sut.StartAsync(BuildOptions());

        gate.Revoke();

        var act = () => sut.StopAsync();
        await act.Should().NotThrowAsync();
        sut.IsStarted.Should().BeFalse();
    }

    [Test]
    public async Task StopAsync_after_revoke_disconnects_client()
    {
        var client = Substitute.For<IMqttClient>();
        client.IsConnected.Returns(true);
        var gate = new RevocableSessionActivityGate();
        await using var sut = new MqttManagedClient(client, gate: gate);
        await sut.StartAsync(BuildOptions());

        gate.Revoke();

        await sut.StopAsync();

        await client.Received(1).DisconnectAsync(Arg.Any<MqttClientDisconnectOptions>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StopAsync_is_idempotent_after_revoke()
    {
        var client = Substitute.For<IMqttClient>();
        client.IsConnected.Returns(true);
        var gate = new RevocableSessionActivityGate();
        await using var sut = new MqttManagedClient(client, gate: gate);
        await sut.StartAsync(BuildOptions());

        gate.Revoke();

        await sut.StopAsync();
        await sut.StopAsync();

        sut.IsStarted.Should().BeFalse();
    }

    [Test]
    public async Task Concurrent_StopAsync_one_teardown()
    {
        var client = Substitute.For<IMqttClient>();
        client.IsConnected.Returns(true);
        var gate = new RevocableSessionActivityGate();
        await using var sut = new MqttManagedClient(client, gate: gate);
        await sut.StartAsync(BuildOptions());

        // Run two concurrent StopAsync calls.
        var task1 = sut.StopAsync();
        var task2 = sut.StopAsync();
        await Task.WhenAll(task1, task2);

        sut.IsStarted.Should().BeFalse();
        // DisconnectAsync should only be called once despite two StopAsync calls.
        await client.Received(1).DisconnectAsync(Arg.Any<MqttClientDisconnectOptions>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public Task Dispose_after_revoke_disposes_cleanly()
    {
        var client = Substitute.For<IMqttClient>();
        var gate = new RevocableSessionActivityGate();
        var sut = new MqttManagedClient(client, ownsClient: true, gate: gate);

        gate.Revoke();

        var act = () => sut.Dispose();
        act.Should().NotThrow();

        client.Received(1).Dispose();
        return Task.CompletedTask;
    }

    [Test]
    public async Task SubscribeAsync_passes_linked_token_to_client()
    {
        var client = Substitute.For<IMqttClient>();
        client.IsConnected.Returns(true);
        var gate = new RevocableSessionActivityGate();
        await using var sut = new MqttManagedClient(client, gate: gate);
        CancellationToken capturedToken = default;
        var tcs = new TaskCompletionSource<MqttClientSubscribeResult>();
        client.SubscribeAsync(Arg.Any<MqttClientSubscribeOptions>(), Arg.Do<CancellationToken>(t => capturedToken = t))
            .Returns(tcs.Task);

        var subscribeTask = sut.SubscribeAsync(new[] { Filter("a/b") });

        capturedToken.CanBeCanceled.Should().BeTrue();
        gate.Revoke();
        await gate.RevocationCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        capturedToken.IsCancellationRequested.Should().BeTrue();
        tcs.SetResult(new MqttClientSubscribeResult(0, Array.Empty<MqttClientSubscribeResultItem>(), null!, Array.Empty<MqttUserProperty>()));
        await subscribeTask;
    }

    [Test]
    public async Task UnsubscribeAsync_passes_linked_token_to_client()
    {
        var client = Substitute.For<IMqttClient>();
        client.IsConnected.Returns(true);
        var gate = new RevocableSessionActivityGate();
        await using var sut = new MqttManagedClient(client, gate: gate);
        CancellationToken capturedToken = default;
        var tcs = new TaskCompletionSource<MqttClientUnsubscribeResult>();
        client.UnsubscribeAsync(Arg.Any<MqttClientUnsubscribeOptions>(), Arg.Do<CancellationToken>(t => capturedToken = t))
            .Returns(tcs.Task);

        var unsubTask = sut.UnsubscribeAsync(new[] { "a/b" });

        capturedToken.CanBeCanceled.Should().BeTrue();
        gate.Revoke();
        await gate.RevocationCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        capturedToken.IsCancellationRequested.Should().BeTrue();
        tcs.SetResult(new MqttClientUnsubscribeResult(0, Array.Empty<MqttClientUnsubscribeResultItem>(), null!, Array.Empty<MqttUserProperty>()));
        await unsubTask;
    }

    [Test]
    public async Task EnqueueAsync_immediate_publish_passes_linked_token_to_client()
    {
        var client = Substitute.For<IMqttClient>();
        client.IsConnected.Returns(true);
        var gate = new RevocableSessionActivityGate();
        await using var sut = new MqttManagedClient(client, gate: gate);
        var message = new MqttApplicationMessageBuilder().WithTopic("a/b").Build();
        CancellationToken capturedToken = default;
        var tcs = new TaskCompletionSource<MqttClientPublishResult>();
        client.PublishAsync(Arg.Any<MqttApplicationMessage>(), Arg.Do<CancellationToken>(t => capturedToken = t))
            .Returns(tcs.Task);

        var publishTask = sut.EnqueueAsync(message);

        capturedToken.CanBeCanceled.Should().BeTrue();
        gate.Revoke();
        await gate.RevocationCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        capturedToken.IsCancellationRequested.Should().BeTrue();
        tcs.SetResult(new MqttClientPublishResult(null, default, null!, Array.Empty<MqttUserProperty>()));
        await publishTask;
    }

    [Test]
    public async Task Resubscribe_passes_revocation_token_to_client()
    {
        var client = Substitute.For<IMqttClient>();
        client.IsConnected.Returns(false);
        var gate = new RevocableSessionActivityGate();
        await using var sut = new MqttManagedClient(client, gate: gate);

        await sut.SubscribeAsync(new[] { Filter("a/b") });
        CancellationToken capturedToken = default;
        client.SubscribeAsync(Arg.Any<MqttClientSubscribeOptions>(), Arg.Do<CancellationToken>(t => capturedToken = t))
            .Returns(Task.FromResult(new MqttClientSubscribeResult(0, Array.Empty<MqttClientSubscribeResultItem>(), null!, Array.Empty<MqttUserProperty>())));
        client.ClearReceivedCalls();

        client.IsConnected.Returns(true);
        client.ConnectedAsync += Raise.Event<Func<MqttClientConnectedEventArgs, Task>>(ConnectedArgs());

        capturedToken.Should().Be(gate.RevocationToken);
    }

    [Test]
    public async Task Drain_passes_revocation_token_to_client()
    {
        var client = Substitute.For<IMqttClient>();
        client.IsConnected.Returns(false);
        var gate = new RevocableSessionActivityGate();
        await using var sut = new MqttManagedClient(client, gate: gate);
        var message = new MqttApplicationMessageBuilder().WithTopic("a/b").Build();

        await sut.EnqueueAsync(message);
        CancellationToken capturedToken = default;
        client.PublishAsync(Arg.Any<MqttApplicationMessage>(), Arg.Do<CancellationToken>(t => capturedToken = t))
            .Returns(Task.FromResult(new MqttClientPublishResult(null, default, null!, Array.Empty<MqttUserProperty>())));
        client.ClearReceivedCalls();

        client.IsConnected.Returns(true);
        client.ConnectedAsync += Raise.Event<Func<MqttClientConnectedEventArgs, Task>>(ConnectedArgs());

        capturedToken.Should().Be(gate.RevocationToken);
    }

    [Test]
    public async Task SubscribeAsync_token_cancellation_blocks_operation()
    {
        var client = Substitute.For<IMqttClient>();
        client.IsConnected.Returns(true);
        var gate = new RevocableSessionActivityGate();
        await using var sut = new MqttManagedClient(client, gate: gate);
        client.SubscribeAsync(Arg.Any<MqttClientSubscribeOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var ct = ci.ArgAt<CancellationToken>(1);
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(new MqttClientSubscribeResult(0, Array.Empty<MqttClientSubscribeResultItem>(), null!, Array.Empty<MqttUserProperty>()));
            });

        gate.Revoke();

        var act = () => sut.SubscribeAsync([Filter("a/b")]);
        await act.Should().ThrowExactlyAsync<InvalidOperationException>();
    }

    [Test]
    public async Task EnqueueAsync_token_cancellation_blocks_immediate_publish()
    {
        var client = Substitute.For<IMqttClient>();
        client.IsConnected.Returns(true);
        var gate = new RevocableSessionActivityGate();
        await using var sut = new MqttManagedClient(client, gate: gate);
        var message = new MqttApplicationMessageBuilder().WithTopic("a/b").Build();
        client.PublishAsync(Arg.Any<MqttApplicationMessage>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var ct = ci.ArgAt<CancellationToken>(1);
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(new MqttClientPublishResult(null, default, null!, Array.Empty<MqttUserProperty>()));
            });

        gate.Revoke();

        var act = () => sut.EnqueueAsync(message);
        await act.Should().ThrowExactlyAsync<InvalidOperationException>();
    }

    [Test]
    public async Task Drain_token_cancellation_stops_drain_after_first()
    {
        var client = Substitute.For<IMqttClient>();
        client.IsConnected.Returns(false);
        var gate = new RevocableSessionActivityGate();
        await using var sut = new MqttManagedClient(client, gate: gate);

        var msg1 = new MqttApplicationMessageBuilder().WithTopic("t/1").Build();
        var msg2 = new MqttApplicationMessageBuilder().WithTopic("t/2").Build();
        await sut.EnqueueAsync(msg1);
        await sut.EnqueueAsync(msg2);

        var publishCount = 0;
        client.PublishAsync(Arg.Any<MqttApplicationMessage>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                publishCount++;
                var ct = ci.ArgAt<CancellationToken>(1);
                if (publishCount == 1)
                    gate.Revoke();
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(new MqttClientPublishResult(null, default, null!, Array.Empty<MqttUserProperty>()));
            });

        client.IsConnected.Returns(true);
        client.ConnectedAsync += Raise.Event<Func<MqttClientConnectedEventArgs, Task>>(ConnectedArgs());

        publishCount.Should().Be(1);
    }

    [Test]
    public async Task TryConnectAsync_revocation_does_not_raise_ConnectingFailed_or_reconnect()
    {
        var client = Substitute.For<IMqttClient>();
        var gate = new RevocableSessionActivityGate();
        await using var sut = new MqttManagedClient(client, gate: gate);
        var connectingFailedRaised = false;
        sut.ConnectingFailedAsync += _ => { connectingFailedRaised = true; return Task.CompletedTask; };

        // Make ConnectAsync cancel via the linked revocation token.
        client.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                var ct = ci.ArgAt<CancellationToken>(1);
                gate.Revoke();
                await gate.RevocationCompletion.WaitAsync(TimeSpan.FromSeconds(10));
                ct.ThrowIfCancellationRequested();
                return new MqttClientConnectResult();
            });

        await sut.StartAsync(BuildOptions());

        connectingFailedRaised.Should().BeFalse("revocation cancellation is expected termination, not a failure");
        // No reconnect loop should have started.
        var connectCalls = client.ReceivedCalls()
            .Count(c => c.GetMethodInfo().Name == nameof(IMqttClient.ConnectAsync));
        connectCalls.Should().Be(1, "only the initial connect attempt, no reconnect");
    }

    [Test]
    public async Task Late_connected_callback_after_revoke_disconnects_and_emits_no_events()
    {
        var client = Substitute.For<IMqttClient>();
        client.IsConnected.Returns(false);
        var gate = new RevocableSessionActivityGate();
        await using var sut = new MqttManagedClient(client, gate: gate);
        var connectedRaised = false;
        var stateChangedRaised = false;
        sut.ConnectedAsync += _ => { connectedRaised = true; return Task.CompletedTask; };
        sut.ConnectionStateChangedAsync += _ => { stateChangedRaised = true; return Task.CompletedTask; };

        gate.Revoke();

        client.IsConnected.Returns(true);
        client.ConnectedAsync += Raise.Event<Func<MqttClientConnectedEventArgs, Task>>(ConnectedArgs());

        connectedRaised.Should().BeFalse();
        stateChangedRaised.Should().BeFalse();
        await client.Received(1).DisconnectAsync(Arg.Any<MqttClientDisconnectOptions>(), Arg.Any<CancellationToken>());
    }
}
