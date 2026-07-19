using AwesomeAssertions;
using MQTTnet;
using MQTTnet.Packets;
using MqttProbe.Services.Mqtt;
using NSubstitute;
using NUnit.Framework;

namespace MqttProbe.Shared.Tests.Services.Mqtt;

[TestFixture]
public class MqttManagedClientTests
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
    public async Task StartAsync_connects_and_sets_started()
    {
        var client = Substitute.For<IMqttClient>();
        await using var sut = new MqttManagedClient(client);

        await sut.StartAsync(BuildOptions());

        sut.IsStarted.Should().BeTrue();
        await client.Received(1).ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StartAsync_connect_failure_raises_ConnectingFailed()
    {
        var client = Substitute.For<IMqttClient>();
        var boom = new InvalidOperationException("refused");
        client.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<MqttClientConnectResult>(boom));

        await using var sut = new MqttManagedClient(client);
        MqttConnectingFailedEventArgs? captured = null;
        sut.ConnectingFailedAsync += e => { captured = e; return Task.CompletedTask; };

        // Large reconnect delay so the background loop does not race the assertion.
        await sut.StartAsync(BuildOptions(TimeSpan.FromMinutes(5)));

        captured.Should().NotBeNull();
        captured!.Exception.Should().BeSameAs(boom);
    }

    [Test]
    public async Task StartAsync_connect_failure_triggers_reconnect()
    {
        var client = Substitute.For<IMqttClient>();
        client.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => Task.FromException<MqttClientConnectResult>(new Exception("boom")),
                _ => Task.FromResult(new MqttClientConnectResult()));

        await using var sut = new MqttManagedClient(client);

        await sut.StartAsync(BuildOptions(TimeSpan.FromMilliseconds(20)));

        // First attempt (StartAsync) fails; reconnect loop makes a second, successful attempt.
        await WaitUntilAsync(() => client.ReceivedCalls()
            .Count(c => c.GetMethodInfo().Name == nameof(IMqttClient.ConnectAsync)) >= 2);

        client.ReceivedCalls()
            .Count(c => c.GetMethodInfo().Name == nameof(IMqttClient.ConnectAsync))
            .Should().BeGreaterThanOrEqualTo(2);
    }

    [Test]
    public async Task StopAsync_disconnects_and_clears_started()
    {
        var client = Substitute.For<IMqttClient>();
        client.IsConnected.Returns(true);
        await using var sut = new MqttManagedClient(client);
        await sut.StartAsync(BuildOptions());

        await sut.StopAsync();

        sut.IsStarted.Should().BeFalse();
        await client.Received(1).DisconnectAsync(Arg.Any<MqttClientDisconnectOptions>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SubscribeAsync_when_connected_subscribes_on_client()
    {
        var client = Substitute.For<IMqttClient>();
        client.IsConnected.Returns(true);
        await using var sut = new MqttManagedClient(client);

        await sut.SubscribeAsync(new[] { Filter("a/b") });

        await client.Received(1).SubscribeAsync(Arg.Any<MqttClientSubscribeOptions>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SubscribeAsync_while_disconnected_defers_then_resubscribes_on_connect()
    {
        var client = Substitute.For<IMqttClient>();
        client.IsConnected.Returns(false);
        await using var sut = new MqttManagedClient(client);

        await sut.SubscribeAsync(new[] { Filter("a/b") });
        await client.DidNotReceive().SubscribeAsync(Arg.Any<MqttClientSubscribeOptions>(), Arg.Any<CancellationToken>());

        client.ConnectedAsync += Raise.Event<Func<MqttClientConnectedEventArgs, Task>>(ConnectedArgs());

        await client.Received(1).SubscribeAsync(Arg.Any<MqttClientSubscribeOptions>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UnsubscribeAsync_when_connected_unsubscribes_on_client()
    {
        var client = Substitute.For<IMqttClient>();
        client.IsConnected.Returns(true);
        await using var sut = new MqttManagedClient(client);

        await sut.UnsubscribeAsync(new[] { "a/b" });

        await client.Received(1).UnsubscribeAsync(Arg.Any<MqttClientUnsubscribeOptions>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EnqueueAsync_when_connected_publishes_immediately()
    {
        var client = Substitute.For<IMqttClient>();
        client.IsConnected.Returns(true);
        await using var sut = new MqttManagedClient(client);
        var message = new MqttApplicationMessageBuilder().WithTopic("a/b").Build();

        await sut.EnqueueAsync(message);

        await client.Received(1).PublishAsync(message, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EnqueueAsync_while_disconnected_queues_then_drains_on_connect()
    {
        var client = Substitute.For<IMqttClient>();
        client.IsConnected.Returns(false);
        await using var sut = new MqttManagedClient(client);
        var message = new MqttApplicationMessageBuilder().WithTopic("a/b").Build();

        await sut.EnqueueAsync(message);
        await client.DidNotReceive().PublishAsync(Arg.Any<MqttApplicationMessage>(), Arg.Any<CancellationToken>());

        client.IsConnected.Returns(true);
        client.ConnectedAsync += Raise.Event<Func<MqttClientConnectedEventArgs, Task>>(ConnectedArgs());

        await client.Received(1).PublishAsync(message, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EnqueueAsync_drops_oldest_when_queue_full()
    {
        var client = Substitute.For<IMqttClient>();
        client.IsConnected.Returns(false);
        await using var sut = new MqttManagedClient(client);

        // Queue bound is 1000; enqueue 1001 so the first is dropped.
        for (var i = 0; i <= 1000; i++)
            await sut.EnqueueAsync(new MqttApplicationMessageBuilder().WithTopic($"msg-{i}").Build());

        client.IsConnected.Returns(true);
        client.ConnectedAsync += Raise.Event<Func<MqttClientConnectedEventArgs, Task>>(ConnectedArgs());

        await client.Received(1000).PublishAsync(Arg.Any<MqttApplicationMessage>(), Arg.Any<CancellationToken>());
        await client.DidNotReceive().PublishAsync(
            Arg.Is<MqttApplicationMessage>(m => m != null && m.Topic == "msg-0"), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ConnectedAsync_event_bubbles_from_client()
    {
        var client = Substitute.For<IMqttClient>();
        await using var sut = new MqttManagedClient(client);
        var raised = false;
        sut.ConnectedAsync += _ => { raised = true; return Task.CompletedTask; };

        client.ConnectedAsync += Raise.Event<Func<MqttClientConnectedEventArgs, Task>>(ConnectedArgs());

        raised.Should().BeTrue();
    }

    [Test]
    public async Task DisconnectedAsync_event_bubbles_from_client()
    {
        var client = Substitute.For<IMqttClient>();
        await using var sut = new MqttManagedClient(client);
        var raised = false;
        sut.DisconnectedAsync += _ => { raised = true; return Task.CompletedTask; };

        client.DisconnectedAsync += Raise.Event<Func<MqttClientDisconnectedEventArgs, Task>>(DisconnectedArgs());

        raised.Should().BeTrue();
    }

    [Test]
    public async Task ApplicationMessageReceivedAsync_bubbles_from_client()
    {
        var client = Substitute.For<IMqttClient>();
        await using var sut = new MqttManagedClient(client);
        var raised = false;
        sut.ApplicationMessageReceivedAsync += _ => { raised = true; return Task.CompletedTask; };

        var message = new MqttApplicationMessageBuilder().WithTopic("a/b").Build();
        var args = new MqttApplicationMessageReceivedEventArgs("client", message, new MqttPublishPacket(), null);
        client.ApplicationMessageReceivedAsync += Raise.Event<Func<MqttApplicationMessageReceivedEventArgs, Task>>(args);

        raised.Should().BeTrue();
    }

    [Test]
    public async Task ConnectionStateChangedAsync_raised_on_connect_and_disconnect()
    {
        var client = Substitute.For<IMqttClient>();
        await using var sut = new MqttManagedClient(client);
        var count = 0;
        sut.ConnectionStateChangedAsync += _ => { count++; return Task.CompletedTask; };

        client.ConnectedAsync += Raise.Event<Func<MqttClientConnectedEventArgs, Task>>(ConnectedArgs());
        client.DisconnectedAsync += Raise.Event<Func<MqttClientDisconnectedEventArgs, Task>>(DisconnectedArgs());

        count.Should().Be(2);
    }

    [Test]
    public void Dispose_disposes_client_when_owned()
    {
        var client = Substitute.For<IMqttClient>();
        var sut = new MqttManagedClient(client, ownsClient: true);

        sut.Dispose();

        client.Received(1).Dispose();
    }

    [Test]
    public void Dispose_does_not_dispose_client_when_not_owned()
    {
        var client = Substitute.For<IMqttClient>();
        var sut = new MqttManagedClient(client, ownsClient: false);

        sut.Dispose();

        client.DidNotReceive().Dispose();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var waited = 0;
        while (!condition() && waited < timeoutMs)
        {
            await Task.Delay(20);
            waited += 20;
        }
    }
}
