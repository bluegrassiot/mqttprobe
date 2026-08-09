using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Packets;

namespace MqttProbe.Core.Services.Mqtt;

// Project-owned managed MQTT client on the MQTTnet 5 IMqttClient. Provides the subset of
// MQTTnet-4 ManagedClient behavior mqttprobe relies on: auto-reconnect with a fixed delay,
// subscription restore after reconnect, and a bounded publish queue that accepts messages
// while disconnected and drains on connect.
public sealed class MqttManagedClient : IMqttManagedClient
{
    private const int MaxPendingMessages = 1000;

    private readonly IMqttClient _client;
    private readonly bool _ownsClient;
    private readonly ILogger<MqttManagedClient>? _logger;

    private readonly Lock _sync = new();
    private readonly Dictionary<string, MqttTopicFilter> _subscriptions = new(StringComparer.Ordinal);
    private readonly Queue<MqttApplicationMessage> _pending = new();

    private MqttManagedClientOptions? _options;
    private CancellationTokenSource? _reconnectCts;
    private Task _reconnectLoop = Task.CompletedTask;
    private bool _isStarted;
    private bool _disposed;

    public MqttManagedClient(ILogger<MqttManagedClient>? logger = null)
        : this(new MqttClientFactory().CreateMqttClient(), ownsClient: true, logger)
    {
    }

    // Test seam.
    public MqttManagedClient(IMqttClient client, bool ownsClient = false, ILogger<MqttManagedClient>? logger = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _ownsClient = ownsClient;
        _logger = logger;

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

        lock (_sync)
        {
            _options = options;
            _isStarted = true;
#pragma warning disable S6966 // cannot await inside a lock; see StopAsync for the async path
            _reconnectCts?.Cancel();
#pragma warning restore S6966
            _reconnectCts?.Dispose();
            _reconnectCts = new CancellationTokenSource();
        }

        await TryConnectAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? cts;
        lock (_sync)
        {
            _isStarted = false;
            cts = _reconnectCts;
            _reconnectCts = null;
            _pending.Clear();
        }

        if (cts is not null)
        {
            // CancelAsync rather than Cancel: registered callbacks then run on the pool
            // instead of synchronously on the thread calling StopAsync.
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

    public async Task SubscribeAsync(IEnumerable<MqttTopicFilter> topicFilters, CancellationToken cancellationToken = default)
    {
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
            await _client.SubscribeAsync(builder.Build(), cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task UnsubscribeAsync(IEnumerable<string> topics, CancellationToken cancellationToken = default)
    {
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
            await _client.UnsubscribeAsync(builder.Build(), cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task EnqueueAsync(MqttApplicationMessage applicationMessage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(applicationMessage);

        if (_client.IsConnected)
        {
            await _client.PublishAsync(applicationMessage, cancellationToken).ConfigureAwait(false);
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

        try
        {
            await _client.ConnectAsync(options.ClientOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            StartReconnectLoop();
            await RaiseAsync(ConnectingFailedAsync, new MqttConnectingFailedEventArgs(ex), nameof(ConnectingFailedAsync))
                .ConfigureAwait(false);
        }
    }

    // Subscribers are observers: one that throws must not starve the rest of the multicast
    // list, and must never take reconnect down with it. MQTTnet swallows exceptions thrown
    // out of its own event handlers, so an escaping fault here disappears silently and the
    // client stops retrying forever.
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

            if (!started || _client.IsConnected)
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
        await ResubscribeAsync().ConfigureAwait(false);
        await DrainPendingAsync().ConfigureAwait(false);

        await RaiseAsync(ConnectedAsync, args, nameof(ConnectedAsync)).ConfigureAwait(false);
        await RaiseConnectionStateChangedAsync().ConfigureAwait(false);
    }

    private async Task OnClientDisconnectedAsync(MqttClientDisconnectedEventArgs args)
    {
        // Before the subscribers, not after: reconnect is this client's own job and must not
        // wait on — or be skipped by — observers such as emulator teardown or a Blazor render.
        bool started;
        lock (_sync)
        {
            started = _isStarted;
        }

        if (started)
            StartReconnectLoop();

        await RaiseAsync(DisconnectedAsync, args, nameof(DisconnectedAsync)).ConfigureAwait(false);
        await RaiseConnectionStateChangedAsync().ConfigureAwait(false);
    }

    private Task OnClientApplicationMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs args) =>
        ApplicationMessageReceivedAsync?.Invoke(args) ?? Task.CompletedTask;

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
            var builder = new MqttClientSubscribeOptionsBuilder();
            foreach (var filter in filters)
                builder.WithTopicFilter(filter);
            await _client.SubscribeAsync(builder.Build(), CancellationToken.None).ConfigureAwait(false);
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
            MqttApplicationMessage? message;
            lock (_sync)
            {
                if (_pending.Count == 0)
                    return;
                message = _pending.Peek();
            }

            try
            {
                await _client.PublishAsync(message, CancellationToken.None).ConfigureAwait(false);
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
