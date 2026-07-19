using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Packets;

namespace MqttProbe.Services.Mqtt;

/// <summary>
/// Project-owned managed MQTT client implemented on the MQTTnet 5 <see cref="IMqttClient"/>.
/// Provides the subset of MQTTnet-4 ManagedClient behavior mqttprobe relies on:
/// auto-reconnect with a fixed delay, subscription restore after reconnect, and a
/// bounded publish queue that accepts messages while disconnected and drains on connect.
/// </summary>
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
            _reconnectCts?.Cancel();
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

        cts?.Cancel();
        cts?.Dispose();

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
            if (ConnectingFailedAsync is not null)
                await ConnectingFailedAsync(new MqttConnectingFailedEventArgs(ex)).ConfigureAwait(false);

            StartReconnectLoop();
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
                if (ConnectingFailedAsync is not null)
                    await ConnectingFailedAsync(new MqttConnectingFailedEventArgs(ex)).ConfigureAwait(false);
            }
        }
    }

    private async Task OnClientConnectedAsync(MqttClientConnectedEventArgs args)
    {
        await ResubscribeAsync().ConfigureAwait(false);
        await DrainPendingAsync().ConfigureAwait(false);

        if (ConnectedAsync is not null)
            await ConnectedAsync(args).ConfigureAwait(false);

        await RaiseConnectionStateChangedAsync().ConfigureAwait(false);
    }

    private async Task OnClientDisconnectedAsync(MqttClientDisconnectedEventArgs args)
    {
        if (DisconnectedAsync is not null)
            await DisconnectedAsync(args).ConfigureAwait(false);

        await RaiseConnectionStateChangedAsync().ConfigureAwait(false);

        bool started;
        lock (_sync)
        {
            started = _isStarted;
        }

        if (started)
            StartReconnectLoop();
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
            await _client.SubscribeAsync(builder.Build()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to restore {Count} subscription(s) after connect", filters.Count);
            if (SynchronizingSubscriptionsFailedAsync is not null)
                await SynchronizingSubscriptionsFailedAsync(new MqttManagedProcessFailedEventArgs(ex)).ConfigureAwait(false);
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
                await _client.PublishAsync(message).ConfigureAwait(false);
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
        ConnectionStateChangedAsync?.Invoke(EventArgs.Empty) ?? Task.CompletedTask;

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
