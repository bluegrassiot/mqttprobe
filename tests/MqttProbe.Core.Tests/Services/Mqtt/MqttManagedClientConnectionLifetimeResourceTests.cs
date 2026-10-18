using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using MQTTnet;
using MqttProbe.Core.Services.Mqtt;

namespace MqttProbe.Core.Tests.Services.Mqtt;

public partial class MqttManagedClientConnectionLifetimeTests
{
    [Test]
    public async Task ReconnectPrunesRetiredLifetimesAndDisposesTheirTokens()
    {
        var reconnects = new ConcurrentQueue<TaskCompletionSource>();
        var connectCount = 0;
        var client = Substitute.For<IMqttClient>();
        var connected = CaptureConnected(client);
        var disconnected = CaptureDisconnected(client);
        client.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                client.IsConnected.Returns(true);
                await connected.Handler!(ConnectedArgs());
                if (Interlocked.Increment(ref connectCount) > 1)
                {
                    if (!reconnects.TryDequeue(out var reconnected))
                        throw new InvalidOperationException("A reconnect completed without a waiting test barrier.");
                    reconnected.TrySetResult();
                }
                return new MqttClientConnectResult { ResultCode = MqttClientConnectResultCode.Success };
            });

        var factory = new QueueClientFactory();
        factory.Enqueue(client);
        await using var sut = new MqttManagedClient(factory, null);
        var attempt = sut.StartAttempt(BuildOptions(TimeSpan.FromMilliseconds(1)));
        (await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Connected);

        for (var index = 0; index < 200; index++)
        {
            var retiredCancellation = GetLifetimeCancellationSource(sut);
            var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            reconnects.Enqueue(reconnected);
            client.IsConnected.Returns(false);
            await disconnected.Handler!(DisconnectedArgs());

            await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await GetReconnectTask(sut).WaitAsync(TimeSpan.FromSeconds(5));
            IsLifetimeDisposed(retiredCancellation).Should().BeTrue();
            GetTrackedLifetimes(sut).Should().ContainSingle();
        }

    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task DisconnectDrainsAdmittedSubscriptionRequests(bool unsubscribe)
    {
        var rawRequestStarted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSubscribe = new TaskCompletionSource<MqttClientSubscribeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseUnsubscribe = new TaskCompletionSource<MqttClientUnsubscribeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = Substitute.For<IMqttClient>();
        var connected = CaptureConnected(client);
        var disconnected = CaptureDisconnected(client);
        client.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                client.IsConnected.Returns(true);
                await connected.Handler!(ConnectedArgs());
                return new MqttClientConnectResult { ResultCode = MqttClientConnectResultCode.Success };
            });
        client.SubscribeAsync(Arg.Any<MqttClientSubscribeOptions>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                if (unsubscribe)
                    return Task.FromResult(SubscribeResult(call.Arg<MqttClientSubscribeOptions>()));
                rawRequestStarted.TrySetResult(call.Arg<CancellationToken>());
                return releaseSubscribe.Task;
            });
        client.UnsubscribeAsync(Arg.Any<MqttClientUnsubscribeOptions>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                rawRequestStarted.TrySetResult(call.Arg<CancellationToken>());
                return releaseUnsubscribe.Task;
            });

        var factory = new QueueClientFactory();
        factory.Enqueue(client);
        await using var sut = new MqttManagedClient(factory, null);
        var attempt = sut.StartAttempt(BuildOptions());
        (await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Connected);
        if (unsubscribe)
            await sut.SubscribeAsync([Filter("held")]);

        var request = unsubscribe
            ? sut.UnsubscribeAsync(["held"])
            : sut.SubscribeAsync([Filter("held")]);
        var requestToken = await rawRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellationRegistration = requestToken.Register(() => cancellationObserved.TrySetResult());
        var lifetimeCancellation = GetLifetimeCancellationSource(sut);
        var retirement = GetLifetimeRetirementTask(sut);

        client.IsConnected.Returns(false);
        await disconnected.Handler!(DisconnectedArgs());
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

        IsLifetimeDisposed(lifetimeCancellation).Should().BeFalse();
        retirement.IsCompleted.Should().BeFalse();
        if (unsubscribe)
            releaseUnsubscribe.SetResult(UnsubscribeResult("held"));
        else
            releaseSubscribe.SetResult(SubscribeResult(new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(Filter("held")).Build()));

        await request.WaitAsync(TimeSpan.FromSeconds(5));
        await retirement.WaitAsync(TimeSpan.FromSeconds(5));
        IsLifetimeDisposed(lifetimeCancellation).Should().BeTrue();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task DisconnectAndShutdownShareLifetimeRetirement(bool dispose)
    {
        var publishStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePublish = new TaskCompletionSource<MqttClientPublishResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = Substitute.For<IMqttClient>();
        var connected = CaptureConnected(client);
        var disconnected = CaptureDisconnected(client);
        client.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                client.IsConnected.Returns(true);
                await connected.Handler!(ConnectedArgs());
                return new MqttClientConnectResult { ResultCode = MqttClientConnectResultCode.Success };
            });
        client.PublishAsync(Arg.Any<MqttApplicationMessage>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                publishStarted.TrySetResult();
                await releasePublish.Task;
                return PublishResult();
            });

        var factory = new QueueClientFactory();
        factory.Enqueue(client);
        await using var sut = new MqttManagedClient(factory, null);
        var attempt = sut.StartAttempt(BuildOptions(TimeSpan.FromMinutes(5)));
        (await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Connected);
        var publish = sut.TryPublishConnectedAsync(Message("held"), sut.ConnectionEpoch);
        await publishStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var lifetimeCancellation = GetLifetimeCancellationSource(sut);
        var generationCancellation = GetGenerationCancellationSource(sut);

        client.IsConnected.Returns(false);
        await disconnected.Handler!(DisconnectedArgs());
        var shutdownCancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var shutdownRegistration = generationCancellation.Token.Register(
            () => shutdownCancellationObserved.TrySetResult());
        var shutdown = dispose ? sut.DisposeAsync().AsTask() : sut.StopAsync();
        await shutdownCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

        IsLifetimeDisposed(lifetimeCancellation).Should().BeFalse();
        shutdown.IsCompleted.Should().BeFalse();
        releasePublish.SetResult(PublishResult());
        await publish.WaitAsync(TimeSpan.FromSeconds(5));
        await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
        IsLifetimeDisposed(lifetimeCancellation).Should().BeTrue();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task FailedOrCancelledSubscribeReleasesLifetimeAdmission(bool cancel)
    {
        var rawRequestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = Substitute.For<IMqttClient>();
        var connected = CaptureConnected(client);
        var disconnected = CaptureDisconnected(client);
        client.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                client.IsConnected.Returns(true);
                await connected.Handler!(ConnectedArgs());
                return new MqttClientConnectResult { ResultCode = MqttClientConnectResultCode.Success };
            });
        client.SubscribeAsync(Arg.Any<MqttClientSubscribeOptions>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                rawRequestStarted.TrySetResult();
                if (!cancel)
                    return Task.FromException<MqttClientSubscribeResult>(new InvalidOperationException("raw subscribe failed"));

                var completion = new TaskCompletionSource<MqttClientSubscribeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                var token = call.Arg<CancellationToken>();
                token.Register(() => completion.TrySetCanceled(token));
                return completion.Task;
            });

        var factory = new QueueClientFactory();
        factory.Enqueue(client);
        await using var sut = new MqttManagedClient(factory, null);
        var attempt = sut.StartAttempt(BuildOptions());
        (await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Connected);
        using var callerCancellation = new CancellationTokenSource();
        var lifetimeCancellation = GetLifetimeCancellationSource(sut);
        var subscribe = sut.SubscribeAsync([Filter("failing")], callerCancellation.Token);
        await rawRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        if (cancel)
        {
            callerCancellation.Cancel();
            await FluentActions.Awaiting(() => subscribe)
                .Should().ThrowAsync<OperationCanceledException>();
        }
        else
        {
            await FluentActions.Awaiting(() => subscribe)
                .Should().ThrowAsync<InvalidOperationException>();
        }

        client.IsConnected.Returns(false);
        await disconnected.Handler!(DisconnectedArgs());
        await GetReconnectDrainTask(sut).WaitAsync(TimeSpan.FromSeconds(5));
        IsLifetimeDisposed(lifetimeCancellation).Should().BeTrue();
    }

    private static CancellationTokenSource GetLifetimeCancellationSource(MqttManagedClient sut) =>
        (CancellationTokenSource)GetCurrentLifetime(sut).GetType()
            .GetProperty("Cancellation", BindingFlags.Instance | BindingFlags.Public)!
            .GetValue(GetCurrentLifetime(sut))!;

    private static object GetCurrentLifetime(MqttManagedClient sut) =>
        GetCurrentGeneration(sut).GetType()
            .GetProperty("Lifetime", BindingFlags.Instance | BindingFlags.Public)!
            .GetValue(GetCurrentGeneration(sut))!;

    private static object GetCurrentGeneration(MqttManagedClient sut) =>
        typeof(MqttManagedClient).GetField("_current", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(sut)!;

    private static IReadOnlyCollection<object> GetTrackedLifetimes(MqttManagedClient sut) =>
        ((IEnumerable)GetCurrentGeneration(sut).GetType()
            .GetProperty("Lifetimes", BindingFlags.Instance | BindingFlags.Public)!
            .GetValue(GetCurrentGeneration(sut))!).Cast<object>().ToArray();

    private static Task GetLifetimeRetirementTask(MqttManagedClient sut) =>
        (Task)GetCurrentLifetime(sut).GetType()
            .GetProperty("RetirementTask", BindingFlags.Instance | BindingFlags.Public)!
            .GetValue(GetCurrentLifetime(sut))!;

    private static Task GetReconnectDrainTask(MqttManagedClient sut) =>
        (Task)GetCurrentGeneration(sut).GetType()
            .GetProperty("ReconnectDrainTask", BindingFlags.Instance | BindingFlags.Public)!
            .GetValue(GetCurrentGeneration(sut))!;

    private static Task GetReconnectTask(MqttManagedClient sut) =>
        (Task)GetCurrentGeneration(sut).GetType()
            .GetProperty("ReconnectTask", BindingFlags.Instance | BindingFlags.Public)!
            .GetValue(GetCurrentGeneration(sut))!;

    private static CancellationTokenSource GetGenerationCancellationSource(MqttManagedClient sut) =>
        (CancellationTokenSource)GetCurrentGeneration(sut).GetType()
            .GetProperty("Cts", BindingFlags.Instance | BindingFlags.Public)!
            .GetValue(GetCurrentGeneration(sut))!;

    private static bool IsLifetimeDisposed(CancellationTokenSource source)
    {
        try
        {
            _ = source.Token;
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }
}
