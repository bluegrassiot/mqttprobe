using MQTTnet;
using MQTTnet.Exceptions;
using MqttProbe.Core.Services.Mqtt;

namespace MqttProbe.Core.Tests.Services.Mqtt;

[TestFixture]
public class MqttManagedClientConnectResultTests
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

    private static MqttClientConnectResult Rejected() =>
        new() { ResultCode = MqttClientConnectResultCode.NotAuthorized };

    private static int ConnectCount(IMqttClient client) =>
        client.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IMqttClient.ConnectAsync));

    [Test]
    public async Task Initial_non_success_result_is_a_failure_with_the_rejection_code_and_retries()
    {
        var calls = 0;
        var client = Substitute.For<IMqttClient>();
        client.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => Interlocked.Increment(ref calls) == 1
                ? Task.FromResult(Rejected())
                : Task.FromResult(Success()));

        var factory = new QueueClientFactory();
        factory.Enqueue(client);
        await using var sut = new MqttManagedClient(factory, null);

        var failures = new List<MqttConnectingFailedEventArgs>();
        var connected = 0;
        sut.ConnectingFailedAsync += e => { lock (failures) failures.Add(e); return Task.CompletedTask; };
        sut.ConnectedAsync += _ => { Interlocked.Increment(ref connected); return Task.CompletedTask; };

        var attempt = sut.StartAttempt(BuildOptions(TimeSpan.FromMilliseconds(20)));
        (await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Failed);

        await WaitUntilAsync(() => Volatile.Read(ref calls) >= 2);

        Volatile.Read(ref calls).Should().BeGreaterThanOrEqualTo(2, "a rejected connect result must retry");
        Volatile.Read(ref connected).Should().Be(0, "a rejected connect must not surface success");
        lock (failures)
        {
            failures.Should().ContainSingle();
            failures[0].Exception.Should().BeOfType<MqttCommunicationException>()
                .Which.Message.Should().Contain(nameof(MqttClientConnectResultCode.NotAuthorized));
        }
    }

    [Test]
    public async Task Reconnect_non_success_result_reports_failure_and_retries_again()
    {
        var calls = 0;
        var client = Substitute.For<IMqttClient>();
        client.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var call = Interlocked.Increment(ref calls);
                if (call == 1)
                    return Task.FromException<MqttClientConnectResult>(new InvalidOperationException("initial"));
                return call == 2 ? Task.FromResult(Rejected()) : Task.FromResult(Success());
            });

        var factory = new QueueClientFactory();
        factory.Enqueue(client);
        await using var sut = new MqttManagedClient(factory, null);

        var failures = 0;
        sut.ConnectingFailedAsync += _ => { Interlocked.Increment(ref failures); return Task.CompletedTask; };

        var attempt = sut.StartAttempt(BuildOptions(TimeSpan.FromMilliseconds(20)));
        (await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Failed);

        await WaitUntilAsync(() => Volatile.Read(ref calls) >= 3);

        Volatile.Read(ref calls).Should().BeGreaterThanOrEqualTo(3, "a rejected reconnect must not exit the loop");
        Volatile.Read(ref failures).Should().Be(2, "the initial failure and the rejected reconnect are both reported");
    }

    [Test]
    public async Task Canceled_blocked_connect_returning_reject_is_silent_canceled()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = Substitute.For<IMqttClient>();
        client.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                started.TrySetResult();
                await release.Task;
                return Rejected();
            });

        var factory = new QueueClientFactory();
        factory.Enqueue(client);
        await using var sut = new MqttManagedClient(factory, null);

        var failures = 0;
        sut.ConnectingFailedAsync += _ => { Interlocked.Increment(ref failures); return Task.CompletedTask; };

        using var cts = new CancellationTokenSource();
        var attempt = sut.StartAttempt(BuildOptions(), cts.Token);
        await started.Task;
        cts.Cancel();
        release.TrySetResult();

        (await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Canceled);
        Volatile.Read(ref failures).Should().Be(0, "a canceled attempt stays silent even if the result is a rejection");
        ConnectCount(client).Should().Be(1, "a canceled attempt must not retry");
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

    private sealed class QueueClientFactory : IMqttClientFactory
    {
        private readonly Queue<IMqttClient> _clients = new();

        public bool OwnsClients => false;

        public void Enqueue(IMqttClient client) => _clients.Enqueue(client);

        public IMqttClient CreateClient() => _clients.Dequeue();
    }
}
