using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Exceptions;

namespace MqttProbe.Core.Services.Mqtt;

public sealed partial class MqttManagedClient
{
    // One explicit Start owns one generation: its own raw client, lifetime token and handlers.
    private sealed class Generation
    {
        private volatile bool _retired;
        private volatile bool _callerRegistrationActive;

        public required MqttConnectAttemptId AttemptId { get; init; }

        public required IMqttClient Client { get; init; }

        public required MqttManagedClientOptions Options { get; init; }

        public required CancellationTokenSource Cts { get; init; }

        public required CancellationToken CallerToken { get; init; }

        public TaskCompletionSource<MqttConnectOutcome> Outcome { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Resource barrier: cleanup awaits this, never a subscriber-notification chain, so a
        // subscriber that awaits Stop cannot hold the teardown open on its own connect op.
        public TaskCompletionSource ConnectCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource OperationCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Stable from birth so a concurrent Stop or Dispose can await it with no assignment race.
        public TaskCompletionSource CleanupCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Func<MqttClientConnectedEventArgs, Task>? ConnectedHandler { get; set; }

        public Func<MqttClientDisconnectedEventArgs, Task>? DisconnectedHandler { get; set; }

        public Func<MqttApplicationMessageReceivedEventArgs, Task>? MessageHandler { get; set; }

        public MqttClientConnectedEventArgs? ConnectedArgs { get; set; }

        public MqttConnectOutcome? SettledOutcome { get; set; }

        public bool Retired
        {
            get => _retired;
            set => _retired = value;
        }

        // Live from the caller's cancellation registration until it is disposed, so a token
        // cancellation revokes ownership even while its callback is still queued.
        public bool CallerRegistrationActive
        {
            get => _callerRegistrationActive;
            set => _callerRegistrationActive = value;
        }

        public bool CleanupStarted { get; set; }

        public bool WasConnected { get; set; }

        public bool StopNotified { get; set; }

        public Task ReconnectTask { get; set; } = Task.CompletedTask;
    }

    private (Generation Generation, MqttConnectAttempt Attempt) BeginAttempt(
        MqttManagedClientOptions options, CancellationToken cancellationToken)
    {
        Generation generation;
        Generation? previous;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_adoptedClient is not null && _adoptedGenerationsUsed > 0)
                throw new InvalidOperationException(
                    "This MqttManagedClient was constructed with an adopted IMqttClient and supports a single " +
                    "connection generation. Construct with an IMqttClientFactory for restartable use.");

            var client = _clientFactory.CreateClient();
            var cts = _activityGate.RevocationToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(_activityGate.RevocationToken)
                : new CancellationTokenSource();

            _nextAttemptId++;
            generation = new Generation
            {
                AttemptId = new MqttConnectAttemptId(_nextAttemptId),
                Client = client,
                Options = options,
                Cts = cts,
                CallerToken = cancellationToken,
            };

            if (_adoptedClient is not null)
                _adoptedGenerationsUsed++;

            previous = _current;
            _current = generation;
            _generations.Add(generation);
        }

        WireGenerationHandlers(generation);

        if (previous is not null)
            RetireGeneration(previous);

        StartConnectOperation(generation, cancellationToken);
        return (generation, new MqttConnectAttempt(generation.AttemptId, generation.Outcome.Task));
    }

    private void StartConnectOperation(Generation generation, CancellationToken callerToken)
    {
        CancellationTokenRegistration registration = default;
        generation.CallerRegistrationActive = true;
        if (callerToken.CanBeCanceled)
            registration = callerToken.Register(() => OnCallerCancellation(generation));

        try
        {
            _ = RunConnectOperationAsync(generation, registration, callerToken);
        }
        catch (Exception ex)
        {
            registration.Dispose();
            _logger?.LogError(ex, "Failed to start the MQTT connect operation");
            CompleteCanceled(generation);
            generation.ConnectCompleted.TrySetResult();
            generation.OperationCompleted.TrySetResult();
        }
    }

    private void OnCallerCancellation(Generation generation)
    {
        RetireGeneration(generation);
        _ = NotifyCallerCancellationAsync(generation);
    }

    private async Task NotifyCallerCancellationAsync(Generation generation)
    {
        await generation.CleanupCompleted.Task.ConfigureAwait(false);
        await NotifyDeliberateStopAsync(generation).ConfigureAwait(false);
    }

    private async Task RunConnectOperationAsync(
        Generation generation, CancellationTokenRegistration callerRegistration, CancellationToken callerToken)
    {
        try
        {
            using var registration = callerRegistration;
            using var linked = LinkToken(callerToken, generation.Cts.Token);

            MqttConnectOutcome outcome;
            if (generation.Retired)
            {
                outcome = CompleteCanceled(generation);
            }
            else
            {
                try
                {
                    var result = await generation.Client.ConnectAsync(generation.Options.ClientOptions, linked.Token)
                        .ConfigureAwait(false);
                    EnsureConnectAccepted(result);
                    outcome = CompleteSuccess(generation, callerToken);
                }
                catch (Exception ex)
                {
                    outcome = CompleteFailure(generation, ex, callerToken);
                }
            }

            generation.ConnectCompleted.TrySetResult();

            if (outcome.Status == MqttConnectStatus.Canceled)
                RetireGeneration(generation);
            else if (outcome.Status == MqttConnectStatus.Failed)
                StartReconnectLoop(generation);

            await NotifyOutcomeAsync(generation, outcome).ConfigureAwait(false);
            generation.OperationCompleted.TrySetResult();
        }
        finally
        {
            generation.CallerRegistrationActive = false;
        }
    }

    private MqttConnectOutcome CompleteSuccess(Generation generation, CancellationToken callerToken)
    {
        lock (_sync)
        {
            if (generation.SettledOutcome is { } settled)
                return settled;

            // Decided under the same lock that retires, so a cancel interleaving after an
            // unlocked check cannot still win. Gate activity is read here because its token
            // cancellation propagates asynchronously.
            var canceled = generation.Retired
                || !ReferenceEquals(_current, generation)
                || generation.Cts.IsCancellationRequested
                || callerToken.IsCancellationRequested
                || !_activityGate.IsActive;

            generation.WasConnected = !canceled;
            var outcome = new MqttConnectOutcome(
                generation.AttemptId,
                canceled ? MqttConnectStatus.Canceled : MqttConnectStatus.Connected);
            generation.SettledOutcome = outcome;
            generation.Outcome.TrySetResult(outcome);
            return outcome;
        }
    }

    private MqttConnectOutcome CompleteFailure(Generation generation, Exception exception, CancellationToken callerToken)
    {
        lock (_sync)
        {
            if (generation.SettledOutcome is { } settled)
                return settled;

            // Classification is by the originating generation, caller and gate, before the
            // exception type: MQTTnet may wrap the OCE, or deliver the failure late.
            var canceled = generation.Retired
                || !ReferenceEquals(_current, generation)
                || generation.Cts.IsCancellationRequested
                || callerToken.IsCancellationRequested
                || !_activityGate.IsActive
                || _activityGate.RevocationToken.IsCancellationRequested;

            var outcome = new MqttConnectOutcome(
                generation.AttemptId,
                canceled ? MqttConnectStatus.Canceled : MqttConnectStatus.Failed,
                canceled ? null : exception);
            generation.SettledOutcome = outcome;
            generation.Outcome.TrySetResult(outcome);
            return outcome;
        }
    }

    private MqttConnectOutcome CompleteCanceled(Generation generation)
    {
        lock (_sync)
        {
            if (generation.SettledOutcome is { } settled)
                return settled;

            var outcome = new MqttConnectOutcome(generation.AttemptId, MqttConnectStatus.Canceled);
            generation.SettledOutcome = outcome;
            generation.Outcome.TrySetResult(outcome);
            return outcome;
        }
    }

    // The originating token decides ownership until its registration is disposed; the generation's
    // own Cts is deliberately not consulted, because gate revocation cancels it while the owned
    // disconnect still has to be delivered.
    private static bool CallerCancellationRequested(Generation generation) =>
        generation.CallerRegistrationActive && generation.CallerToken.IsCancellationRequested;

    private bool IsOwnedForWork(Generation generation) =>
        !generation.Retired
        && !CallerCancellationRequested(generation)
        && ReferenceEquals(Volatile.Read(ref _current), generation);

    private bool IsEligibleForWork(Generation generation) =>
        IsOwnedForWork(generation) && _activityGate.IsActive;

    private bool IsOwnedForWorkLocked(Generation generation) =>
        !generation.Retired
        && !CallerCancellationRequested(generation)
        && ReferenceEquals(_current, generation);

    private void RetireGeneration(Generation generation)
    {
        bool startCleanup = false;
        lock (_sync)
        {
            if (!generation.Retired)
            {
                generation.Retired = true;
                if (ReferenceEquals(_current, generation))
                    _current = null;
            }

            if (!generation.CleanupStarted)
            {
                generation.CleanupStarted = true;
                startCleanup = true;
            }
        }

        CancelQuietly(generation.Cts);

        if (startCleanup)
            _ = CleanupGenerationAsync(generation);
    }

    private async Task CleanupGenerationAsync(Generation generation)
    {
        try
        {
            await generation.ConnectCompleted.Task.ConfigureAwait(false);
            await AwaitQuietlyAsync(generation.ReconnectTask).ConfigureAwait(false);

            UnwireGenerationHandlers(generation);

            if (generation.Client.IsConnected)
                await DisconnectQuietlyAsync(generation.Client).ConfigureAwait(false);

            if (_clientFactory.OwnsClients)
                DisposeQuietly(generation.Client);
        }
        finally
        {
            generation.Cts.Dispose();
            lock (_sync)
            {
                _generations.Remove(generation);
            }

            generation.CleanupCompleted.TrySetResult();
        }
    }

    private void WireGenerationHandlers(Generation generation)
    {
        generation.ConnectedHandler = args => OnGenerationConnectedAsync(generation, args);
        generation.DisconnectedHandler = args => OnGenerationDisconnectedAsync(generation, args);
        generation.MessageHandler = args => OnGenerationApplicationMessageReceivedAsync(generation, args);

        generation.Client.ConnectedAsync += generation.ConnectedHandler;
        generation.Client.DisconnectedAsync += generation.DisconnectedHandler;
        generation.Client.ApplicationMessageReceivedAsync += generation.MessageHandler;
    }

    private static void UnwireGenerationHandlers(Generation generation)
    {
        if (generation.ConnectedHandler is not null)
            generation.Client.ConnectedAsync -= generation.ConnectedHandler;
        if (generation.DisconnectedHandler is not null)
            generation.Client.DisconnectedAsync -= generation.DisconnectedHandler;
        if (generation.MessageHandler is not null)
            generation.Client.ApplicationMessageReceivedAsync -= generation.MessageHandler;
    }

    private void StartReconnectLoop(Generation generation)
    {
        lock (_sync)
        {
            if (!IsOwnedForWorkLocked(generation))
                return;
            if (!generation.ReconnectTask.IsCompleted)
                return;

            generation.ReconnectTask = ReconnectLoopAsync(generation);
        }
    }

    private async Task ReconnectLoopAsync(Generation generation)
    {
        var cancellationToken = generation.Cts.Token;
        var delay = generation.Options.AutoReconnectDelay;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!IsOwnedForWork(generation) || generation.Client.IsConnected || !_activityGate.IsActive)
                return;

            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!IsOwnedForWork(generation) || !_activityGate.IsActive)
                return;

            try
            {
                var result = await generation.Client.ConnectAsync(generation.Options.ClientOptions, cancellationToken)
                    .ConfigureAwait(false);
                EnsureConnectAccepted(result);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                if (!IsEligibleForWork(generation) || cancellationToken.IsCancellationRequested)
                    return;

                _ = NotifyReconnectFailureAsync(generation, ex);
            }
        }
    }

    private static void EnsureConnectAccepted(MqttClientConnectResult result)
    {
        if (result.ResultCode != MqttClientConnectResultCode.Success)
            throw new MqttCommunicationException($"MQTT connection rejected: {result.ResultCode}.");
    }

    private async Task OnGenerationConnectedAsync(Generation generation, MqttClientConnectedEventArgs args)
    {
        if (!IsEligibleForWork(generation))
        {
            await DisconnectQuietlyAsync(generation.Client).ConfigureAwait(false);
            return;
        }

        await ResubscribeAsync(generation).ConfigureAwait(false);
        await DrainPendingAsync(generation).ConfigureAwait(false);

        if (!IsEligibleForWork(generation))
        {
            await DisconnectQuietlyAsync(generation.Client).ConfigureAwait(false);
            return;
        }

        lock (_sync)
        {
            if (IsOwnedForWorkLocked(generation) && _activityGate.IsActive)
                generation.WasConnected = true;
        }

        // Notified outside the raw connect op: a Connected subscriber that awaits Stop would
        // otherwise hold the resource barrier open.
        if (generation.ConnectCompleted.Task.IsCompleted)
            _ = NotifyConnectedAsync(generation, args);
        else
            generation.ConnectedArgs = args;
    }

    private Task OnGenerationDisconnectedAsync(Generation generation, MqttClientDisconnectedEventArgs args)
    {
        if (!IsOwnedForWork(generation))
            return Task.CompletedTask;

        if (_activityGate.IsActive)
            StartReconnectLoop(generation);

        _ = NotifyDisconnectedAsync(generation, args);
        return Task.CompletedTask;
    }

    private Task OnGenerationApplicationMessageReceivedAsync(
        Generation generation, MqttApplicationMessageReceivedEventArgs args)
    {
        if (!IsOwnedForWork(generation) || !_activityGate.IsActive)
            return Task.CompletedTask;

        return ApplicationMessageReceivedAsync?.Invoke(args) ?? Task.CompletedTask;
    }
}
