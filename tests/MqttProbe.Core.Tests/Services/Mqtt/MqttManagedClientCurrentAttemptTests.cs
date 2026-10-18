using MQTTnet;
using MQTTnet.Exceptions;
using MqttProbe.Core.Services.Mqtt;

namespace MqttProbe.Core.Tests.Services.Mqtt;

[TestFixture]
public class MqttManagedClientCurrentAttemptTests
{
    private static MqttManagedClientOptions BuildOptions(TimeSpan? reconnect = null) => new()
    {
        ClientOptions = new MqttClientOptionsBuilder()
            .WithTcpServer("localhost", 1883)
            .WithClientId("test")
            .Build(),
        AutoReconnectDelay = reconnect ?? TimeSpan.FromMinutes(5)
    };

    private static MqttClientConnectResult Success() =>
        new() { ResultCode = MqttClientConnectResultCode.Success };

    [Test]
    public async Task IsCurrentAttempt_follows_the_retrying_generation_until_it_is_replaced_or_stopped()
    {
        var calls = 0;
        var connected = 0;
        var client = Substitute.For<IMqttClient>();
        var connectedHandler = CaptureConnected(client);
        client.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                    throw new MqttCommunicationException("refused");

                await connectedHandler.Handler!(new MqttClientConnectedEventArgs(new MqttClientConnectResult()));
                return Success();
            });

        var factory = new QueueClientFactory();
        factory.Enqueue(client);
        factory.Enqueue(Substitute.For<IMqttClient>());
        await using var sut = new MqttManagedClient(factory, null);
        sut.ConnectedAsync += _ =>
        {
            Interlocked.Increment(ref connected);
            return Task.CompletedTask;
        };

        sut.IsCurrentAttempt(MqttConnectAttemptId.None).Should().BeFalse(
            "no attempt id identifies a generation");

        var attempt = sut.StartAttempt(BuildOptions(TimeSpan.FromMilliseconds(20)));
        (await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Failed);

        await WaitUntilAsync(() => Volatile.Read(ref connected) == 1);

        Volatile.Read(ref connected).Should().Be(1,
            "a failed generation reconnects and reports it without a new attempt id");
        sut.IsCurrentAttempt(attempt.Id).Should().BeTrue(
            "the reconnecting generation stays the current attempt");

        var replacement = sut.StartAttempt(BuildOptions());
        sut.IsCurrentAttempt(attempt.Id).Should().BeFalse(
            "a newer generation supersedes the one that was still retrying");

        await sut.StopAttemptAsync(replacement.Id);
        sut.IsCurrentAttempt(replacement.Id).Should().BeFalse("a stopped attempt is no longer current");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var waited = 0;
        while (!condition() && waited < timeoutMs)
        {
            await Task.Delay(20);
            waited += 20;
        }
    }

    private static HandlerCapture<MqttClientConnectedEventArgs> CaptureConnected(IMqttClient client)
    {
        var capture = new HandlerCapture<MqttClientConnectedEventArgs>();
        client.When(c => c.ConnectedAsync += Arg.Any<Func<MqttClientConnectedEventArgs, Task>>())
            .Do(ci => capture.Handler = ci.Arg<Func<MqttClientConnectedEventArgs, Task>>());
        return capture;
    }

    private sealed class HandlerCapture<T>
    {
        public Func<T, Task>? Handler { get; set; }
    }

    private sealed class QueueClientFactory : IMqttClientFactory
    {
        private readonly Queue<IMqttClient> _clients = new();

        public bool OwnsClients => false;

        public void Enqueue(IMqttClient client) => _clients.Enqueue(client);

        public IMqttClient CreateClient() => _clients.Dequeue();
    }
}
