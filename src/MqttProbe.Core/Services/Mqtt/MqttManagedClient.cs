using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Packets;

namespace MqttProbe.Core.Services.Mqtt;

public sealed class MqttManagedClient : IMqttManagedClient
{
    private const int MaxPendingMessages = 1000;

    private readonly IMqttClient _client;
    private readonly bool _ownsClient;
    private readonly ILogger<MqttManagedClient>? _logger;
    private readonly ISessionActivityGate _activityGate;

    private readonly Lock _sync = new();
    private readonly Dictionary<string, MqttTopicFilter> _subscriptions = new(StringComparer.Ordinal);
    private readonly Queue<MqttApplicationMessage> _pending = new();
    private readonly SemaphoreSlim _stopSemaphore = new(1, 1);

    private MqttManagedClientOptions? _options;
    private CancellationTokenSource? _reconnectCts;
    private Task _reconnectLoop = Task.CompletedTask;
    private bool _isStarted;
    private bool _disposed;

    public MqttManagedClient(ILogger<MqttManagedClient>? logger = null, ISessionActivityGate? gate = null)
        : this(new MqttClientFactory().CreateMqttClient(), ownsClient: true, logger, gate)
    {
    }

    public MqttManagedClient(IMqttClient client, bool ownsClient = false, ILogger<MqttManagedClient>? logger = null, ISessionActivityGate? gate = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _ownsClient = ownsClient;
        _logger = logger;
        _activityGate = gate ?? new AlwaysActiveSessionActivityGate();

        _client.ConnectedAsync += OnClientConnectedAsync;
        _client.DisconnectedAsync += OnClientDisconnectedAsync;
        _client.ApplicationMessageReceivedAsync += OnClientApplicationMessageReceivedAsync;
    }

    public bool IsConnected => _client.IsConnected;

    public bool IsStarted
    {
        get { lock (_sync) { return _isStarted; } }
    }

    public event Func<MqttClientConnectedEventArgs, Task>? ConnectedAsync;
    public event Func<MqttClientDisconnectedEventArgs, Task>? DisconnectedAsync;
    public event Func<MqttConnectingFailedEventArgs, Task>? ConnectingFailedAsync;
    public event Func<EventArgs, Task>? ConnectionStateChangedAsync;
    public event Func<MqttApplicationMessageReceivedEventArgs, Task>? ApplicationMessageReceivedAsync;
    public event Func<MqttManagedProcessFailedEventArgs, Task>? SynchronizingSubscriptionsFailedAsync;

    public async Task StartAsync(MqttManagedClientOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        _activityGate.EnsureActive();

        lock (_sync)
        {
            _options = options;
            _isStarted = true;
#pragma warning disable S6966
            _reconnectCts?.Cancel();
#pragma warning restore S6966
            _reconnectCts?.Dispose();
            _reconnectCts = _activityGate.RevocationToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(_activityGate.RevocationToken)
                : new CancellationTokenSource();
        }

        await TryConnectAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _stopSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CancellationTokenSource? cts;
            lock (_sync)
            {
                if (!_isStarted)
                    return;
                _isStarted = false;
                cts = _reconnectCts;
                _reconnectCts = null;
                _pending.Clear();
            }

            if (cts is not null)
            {
                await cts.CancelAsync().ConfigureAwait(false);
                cts.Dispose();
            }

            if (_client.IsConnected)
            {
                try
                {
                    await _client.DisconnectAsync(new MqttClientDisconnectOptions(), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Error while disconnecting MQTT client during stop");
                }
            }
        }
        finally
        {
            _stopSemaphore.Release();
        }
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

        if (_client.IsConnected)
        {
            var builder = new MqttClientSubscribeOptionsBuilder();
            foreach (var filter in filters)
                builder.WithTopicFilter(filter);
            using var linked = LinkWithRevocation(cancellationToken);
            var token = linked?.Token ?? cancellationToken;
            await _client.SubscribeAsync(builder.Build(), token).ConfigureAwait(false);
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

        if (_client.IsConnected)
        {
            var builder = new MqttClientUnsubscribeOptionsBuilder();
            foreach (var topic in list)
                builder.WithTopicFilter(topic);
            using var linked = LinkWithRevocation(cancellationToken);
            var token = linked?.Token ?? cancellationToken;
            await _client.UnsubscribeAsync(builder.Build(), token).ConfigureAwait(false);
        }
    }

    public async Task EnqueueAsync(MqttApplicationMessage applicationMessage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(applicationMessage);
        _activityGate.EnsureActive();

        if (_client.IsConnected)
        {
            using var linked = LinkWithRevocation(cancellationToken);
            var token = linked?.Token ?? cancellationToken;
            await _client.PublishAsync(applicationMessage, token).ConfigureAwait(false);
            return;
        }

        lock (_sync)
        {
            if (_pending.Count >= MaxPendingMessages)
            {
                _pending.Dequeue();
                _logger?.LogWarning(
                    "Pending publish queue full ({Max}); dropped oldest message for topic {Topic}",
                    MaxPendingMessages, applicationMessage.Topic);
            }

            _pending.Enqueue(applicationMessage);
        }
    }

    private async Task TryConnectAsync(CancellationToken cancellationToken)
    {
        MqttManagedClientOptions? options;
        lock (_sync)
        {
            options = _options;
        }

        if (options is null)
            return;

        var linkedToken = _activityGate.RevocationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _activityGate.RevocationToken)
            : null;
        try
        {
            var token = linkedToken?.Token ?? cancellationToken;
            await _client.ConnectAsync(options.ClientOptions, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_activityGate.RevocationToken.IsCancellationRequested)
        {
            // Revocation during initial connect is expected session termination;
            // do not schedule reconnect or raise ConnectingFailed.
        }
        catch (Exception ex)
        {
            StartReconnectLoop();
            await RaiseAsync(ConnectingFailedAsync, new MqttConnectingFailedEventArgs(ex), nameof(ConnectingFailedAsync))
                .ConfigureAwait(false);
        }
        finally
        {
            linkedToken?.Dispose();
        }
    }

    private async Task RaiseAsync<TArgs>(Func<TArgs, Task>? handler, TArgs args, string eventName)
    {
        if (handler is null)
            return;

        foreach (var subscriber in handler.GetInvocationList())
        {
            try
            {
                await ((Func<TArgs, Task>)subscriber)(args).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "A {EventName} subscriber threw; continuing", eventName);
            }
        }
    }

    private void StartReconnectLoop()
    {
        lock (_sync)
        {
            if (!_isStarted || _reconnectCts is null)
                return;
            if (!_reconnectLoop.IsCompleted)
                return;

            _reconnectLoop = ReconnectLoopAsync(_reconnectCts.Token);
        }
    }

    private async Task ReconnectLoopAsync(CancellationToken cancellationToken)
    {
        var delay = _options?.AutoReconnectDelay ?? TimeSpan.FromSeconds(5);

        while (!cancellationToken.IsCancellationRequested)
        {
            bool started;
            lock (_sync)
            {
                started = _isStarted;
            }

            if (!started || _client.IsConnected || !_activityGate.IsActive)
                return;

            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            MqttManagedClientOptions? options;
            lock (_sync)
            {
                options = _isStarted ? _options : null;
            }

            if (options is null)
                return;

            try
            {
                await _client.ConnectAsync(options.ClientOptions, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                await RaiseAsync(ConnectingFailedAsync, new MqttConnectingFailedEventArgs(ex), nameof(ConnectingFailedAsync))
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task OnClientConnectedAsync(MqttClientConnectedEventArgs args)
    {
        if (!_activityGate.IsActive)
        {
            // Connected callback arrived after session revocation. Disconnect immediately
            // without resubscribing, draining, or emitting any application events.
            if (_client.IsConnected)
            {
                try { await _client.DisconnectAsync(new MqttClientDisconnectOptions(), CancellationToken.None).ConfigureAwait(false); }
                catch { /* best-effort disconnect after late connect */ }
            }
            return;
        }

        await ResubscribeAsync().ConfigureAwait(false);
        await DrainPendingAsync().ConfigureAwait(false);

        await RaiseAsync(ConnectedAsync, args, nameof(ConnectedAsync)).ConfigureAwait(false);
        await RaiseConnectionStateChangedAsync().ConfigureAwait(false);
    }

    private async Task OnClientDisconnectedAsync(MqttClientDisconnectedEventArgs args)
    {
        bool started;
        lock (_sync)
        {
            started = _isStarted;
        }

        if (started && _activityGate.IsActive)
            StartReconnectLoop();

        await RaiseAsync(DisconnectedAsync, args, nameof(DisconnectedAsync)).ConfigureAwait(false);
        await RaiseConnectionStateChangedAsync().ConfigureAwait(false);
    }

    private Task OnClientApplicationMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs args)
    {
        if (!_activityGate.IsActive)
            return Task.CompletedTask;

        return ApplicationMessageReceivedAsync?.Invoke(args) ?? Task.CompletedTask;
    }

    private async Task ResubscribeAsync()
    {
        List<MqttTopicFilter> filters;
        lock (_sync)
        {
            if (_subscriptions.Count == 0)
                return;
            filters = _subscriptions.Values.ToList();
        }

        try
        {
            if (!_activityGate.IsActive) return;
            var builder = new MqttClientSubscribeOptionsBuilder();
            foreach (var filter in filters)
                builder.WithTopicFilter(filter);
            await _client.SubscribeAsync(builder.Build(), _activityGate.RevocationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_activityGate.RevocationToken.IsCancellationRequested)
        {
            // Revocation during resubscribe is expected; suppress.
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to restore {Count} subscription(s) after connect", filters.Count);
            await RaiseAsync(SynchronizingSubscriptionsFailedAsync, new MqttManagedProcessFailedEventArgs(ex),
                nameof(SynchronizingSubscriptionsFailedAsync)).ConfigureAwait(false);
        }
    }

    private async Task DrainPendingAsync()
    {
        while (true)
        {
            if (!_activityGate.IsActive)
                return;

            MqttApplicationMessage? message;
            lock (_sync)
            {
                if (_pending.Count == 0)
                    return;
                message = _pending.Peek();
            }

            try
            {
                await _client.PublishAsync(message, _activityGate.RevocationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_activityGate.RevocationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to publish queued message for topic {Topic}; will retry on next connect", message.Topic);
                return;
            }

            lock (_sync)
            {
                if (_pending.Count > 0)
                    _pending.Dequeue();
            }
        }
    }

    private Task RaiseConnectionStateChangedAsync() =>
        RaiseAsync(ConnectionStateChangedAsync, EventArgs.Empty, nameof(ConnectionStateChangedAsync));

    private CancellationTokenSource? LinkWithRevocation(CancellationToken cancellationToken)
    {
        if (!_activityGate.RevocationToken.CanBeCanceled)
            return null;
        if (!cancellationToken.CanBeCanceled)
            return CancellationTokenSource.CreateLinkedTokenSource(_activityGate.RevocationToken);
        return CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _activityGate.RevocationToken);
    }

    public void Dispose()
    {
        CancellationTokenSource? cts;
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            _isStarted = false;
            cts = _reconnectCts;
            _reconnectCts = null;
        }

        cts?.Cancel();
        cts?.Dispose();
        _stopSemaphore.Dispose();

        _client.ConnectedAsync -= OnClientConnectedAsync;
        _client.DisconnectedAsync -= OnClientDisconnectedAsync;
        _client.ApplicationMessageReceivedAsync -= OnClientApplicationMessageReceivedAsync;

        if (_ownsClient)
            _client.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
