using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Packets;

namespace MqttProbe.Core.Services.Mqtt;

public sealed partial class MqttManagedClient : IMqttManagedClient
{
    private const int MaxPendingMessages = 1000;

    private readonly IMqttClientFactory _clientFactory;
    private readonly IMqttClient? _adoptedClient;
    private readonly bool _ownsAdoptedClient;
    private readonly ILogger<MqttManagedClient>? _logger;
    private readonly ISessionActivityGate _activityGate;

    private readonly Lock _sync = new();
    private readonly Dictionary<string, MqttTopicFilter> _subscriptions = new(StringComparer.Ordinal);
    private readonly Queue<MqttApplicationMessage> _pending = new();
    private readonly HashSet<Generation> _generations = [];
    private readonly SemaphoreSlim _stopSemaphore = new(1, 1);
    private readonly SemaphoreSlim _drainSemaphore = new(1, 1);
    private readonly AsyncLocal<long?> _messageConnectionEpoch = new();

    private Generation? _current;
    private long _nextAttemptId;
    private long _pendingHeadRevision;
    private int _adoptedGenerationsUsed;
    private bool _adoptedClientDisposed;
    private bool _disposed;

    public MqttManagedClient(ILogger<MqttManagedClient>? logger = null, ISessionActivityGate? gate = null)
        : this(new MqttNetClientFactory(), logger, gate)
    {
    }

    public MqttManagedClient(IMqttClientFactory clientFactory, ILogger<MqttManagedClient>? logger, ISessionActivityGate? gate = null)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _logger = logger;
        _activityGate = gate ?? new AlwaysActiveSessionActivityGate();
    }

    // An adopted client backs one generation; use IMqttClientFactory to restart.
    public MqttManagedClient(IMqttClient client, bool ownsClient = false, ILogger<MqttManagedClient>? logger = null, ISessionActivityGate? gate = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _clientFactory = new AdoptedClientFactory(client);
        _adoptedClient = client;
        _ownsAdoptedClient = ownsClient;
        _logger = logger;
        _activityGate = gate ?? new AlwaysActiveSessionActivityGate();
    }

    public bool IsConnected
    {
        get
        {
            lock (_sync) { return _current?.Client.IsConnected ?? false; }
        }
    }

    public bool IsStarted
    {
        get { lock (_sync) { return _current is not null; } }
    }

    public long? ConnectionEpoch
    {
        get
        {
            if (_messageConnectionEpoch.Value is { } eventEpoch)
                return eventEpoch;
            lock (_sync)
                return _current is { Client.IsConnected: true, Retired: false } generation
                    ? generation.AttemptId.Value
                    : null;
        }
    }

    public event Func<MqttClientConnectedEventArgs, Task>? ConnectedAsync;
    public event Func<MqttClientDisconnectedEventArgs, Task>? DisconnectedAsync;
    public event Func<MqttConnectingFailedEventArgs, Task>? ConnectingFailedAsync;
    public event Func<EventArgs, Task>? ConnectionStateChangedAsync;
    public event Func<MqttApplicationMessageReceivedEventArgs, Task>? ApplicationMessageReceivedAsync;
    public event Func<MqttManagedProcessFailedEventArgs, Task>? SynchronizingSubscriptionsFailedAsync;
    public event Func<MqttConnectOutcome, Task>? ConnectAttemptCompletedAsync;

    private IMqttClient? ActiveClient
    {
        get { lock (_sync) { return _current?.Client; } }
    }

    public async Task StartAsync(MqttManagedClientOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        _activityGate.EnsureActive();

        var (generation, _) = BeginAttempt(options, cancellationToken);
        await generation.OperationCompleted.Task.ConfigureAwait(false);

        MqttConnectStatus? settled;
        lock (_sync) { settled = generation.SettledOutcome?.Status; }

        if (cancellationToken.IsCancellationRequested && settled == MqttConnectStatus.Canceled)
        {
            throw new OperationCanceledException(
                "The connection attempt was canceled by the caller.", cancellationToken);
        }
    }

    public MqttConnectAttempt StartAttempt(MqttManagedClientOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        _activityGate.EnsureActive();

        var (_, attempt) = BeginAttempt(options, cancellationToken);
        return attempt;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Generation[] generations;
        Generation? stopped;
        await _stopSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                generations = _generations.ToArray();
                stopped = _current;
                _pending.Clear();
                _pendingHeadRevision++;
            }

            foreach (var generation in generations)
                RetireGeneration(generation);

            // Await every retiring generation, not just the (possibly already cleared)
            // current one, so a caller that disposes certificate material after Stop cannot
            // free it while a generation is still using it.
            foreach (var generation in generations)
                await generation.CleanupCompleted.Task.ConfigureAwait(false);
        }
        finally
        {
            _stopSemaphore.Release();
        }

        // Emitted after releasing the semaphore: a Disconnected subscriber may itself call Stop.
        if (stopped is not null)
            await NotifyDeliberateStopAsync(stopped).ConfigureAwait(false);
    }

    public async Task StopAttemptAsync(MqttConnectAttemptId attemptId, CancellationToken cancellationToken = default)
    {
        Generation? target;
        lock (_sync)
        {
            target = _generations.FirstOrDefault(generation => generation.AttemptId == attemptId);
        }

        if (target is null)
            return;

        RetireGeneration(target);
        await target.CleanupCompleted.Task.ConfigureAwait(false);
        await NotifyDeliberateStopAsync(target).ConfigureAwait(false);
    }

    public async Task SubscribeAsync(IEnumerable<MqttTopicFilter> topicFilters, CancellationToken cancellationToken = default)
    {
        _activityGate.EnsureActive();
        var filters = topicFilters.ToList();
        if (filters.Count == 0)
            return;

        lock (_sync)
        {
            foreach (var filter in filters)
                _subscriptions[filter.Topic] = filter;
        }

        if (ActiveClient is { IsConnected: true } client)
        {
            var builder = new MqttClientSubscribeOptionsBuilder();
            foreach (var filter in filters)
                builder.WithTopicFilter(filter);
            using var linked = LinkWithRevocation(cancellationToken);
            var token = linked?.Token ?? cancellationToken;
            await client.SubscribeAsync(builder.Build(), token).ConfigureAwait(false);
        }
    }

    public async Task UnsubscribeAsync(IEnumerable<string> topics, CancellationToken cancellationToken = default)
    {
        _activityGate.EnsureActive();
        var list = topics.ToList();
        if (list.Count == 0)
            return;

        lock (_sync)
        {
            foreach (var topic in list)
                _subscriptions.Remove(topic);
        }

        if (ActiveClient is { IsConnected: true } client)
        {
            var builder = new MqttClientUnsubscribeOptionsBuilder();
            foreach (var topic in list)
                builder.WithTopicFilter(topic);
            using var linked = LinkWithRevocation(cancellationToken);
            var token = linked?.Token ?? cancellationToken;
            await client.UnsubscribeAsync(builder.Build(), token).ConfigureAwait(false);
        }
    }

    public async Task EnqueueAsync(MqttApplicationMessage applicationMessage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(applicationMessage);
        _activityGate.EnsureActive();

        if (ActiveClient is { IsConnected: true } client)
        {
            using var linked = LinkWithRevocation(cancellationToken);
            var token = linked?.Token ?? cancellationToken;
            await client.PublishAsync(applicationMessage, token).ConfigureAwait(false);
            return;
        }

        lock (_sync)
        {
            if (_pending.Count >= MaxPendingMessages)
            {
                _pending.Dequeue();
                _pendingHeadRevision++;
                _logger?.LogWarning(
                    "Pending publish queue full ({Max}); dropped oldest message for topic {Topic}",
                    MaxPendingMessages, applicationMessage.Topic);
            }

            _pending.Enqueue(applicationMessage);
        }
    }

    public async Task<bool> TryPublishConnectedAsync(
        MqttApplicationMessage applicationMessage,
        long? expectedConnectionEpoch = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(applicationMessage);
        _activityGate.EnsureActive();

        Generation? generation;
        lock (_sync)
            generation = _current;
        if (generation is null || !generation.Client.IsConnected || !IsOwnedForWork(generation))
            return false;
        if (expectedConnectionEpoch is { } expectedEpoch && generation.AttemptId.Value != expectedEpoch)
            return false;

        using var linked = CreatePublishTokenSource(cancellationToken, generation.Cts.Token);

        lock (_sync)
        {
            if (!ReferenceEquals(_current, generation)
                || generation.Retired
                || !generation.Client.IsConnected
                || expectedConnectionEpoch is { } expected && generation.AttemptId.Value != expected)
                return false;
        }

        await generation.Client.PublishAsync(applicationMessage, linked.Token).ConfigureAwait(false);
        return true;
    }

    private CancellationTokenSource CreatePublishTokenSource(CancellationToken cancellationToken, CancellationToken generationToken)
    {
        var tokens = new List<CancellationToken>(3);
        if (cancellationToken.CanBeCanceled)
            tokens.Add(cancellationToken);
        tokens.Add(generationToken);
        if (_activityGate.RevocationToken.CanBeCanceled)
            tokens.Add(_activityGate.RevocationToken);
        return CancellationTokenSource.CreateLinkedTokenSource(tokens.ToArray());
    }

    public void Dispose()
    {
        _ = InitiateDispose();
        DisposeAdoptedClient();

        // _stopSemaphore is left undisposed on purpose: a StopAsync already past WaitAsync
        // must still be able to Release. Disposing here would turn its finally into an
        // ObjectDisposedException and strand the disconnect it was performing.
    }

    public async ValueTask DisposeAsync()
    {
        var generations = InitiateDispose();

        foreach (var generation in generations)
            await generation.CleanupCompleted.Task.ConfigureAwait(false);

        DisposeAdoptedClient();
    }

    // Initiates disposal once, but returns the tracked generations every caller must await:
    // a synchronous Dispose does not release another caller from the cleanup barrier.
    private Generation[] InitiateDispose()
    {
        Generation[] generations;
        lock (_sync)
        {
            _disposed = true;
            generations = _generations.ToArray();
            _current = null;
        }

        foreach (var generation in generations)
            RetireGeneration(generation);

        return generations;
    }

    private void DisposeAdoptedClient()
    {
        if (_adoptedClient is not { } client)
            return;

        bool dispose;
        lock (_sync)
        {
            dispose = _ownsAdoptedClient && !_adoptedClientDisposed;
            if (dispose)
                _adoptedClientDisposed = true;
        }

        if (dispose)
            client.Dispose();
    }

    private CancellationTokenSource? LinkWithRevocation(CancellationToken cancellationToken)
    {
        if (!_activityGate.RevocationToken.CanBeCanceled)
            return null;
        if (!cancellationToken.CanBeCanceled)
            return CancellationTokenSource.CreateLinkedTokenSource(_activityGate.RevocationToken);
        return CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _activityGate.RevocationToken);
    }
}
