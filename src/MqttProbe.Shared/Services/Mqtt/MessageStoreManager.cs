using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Logging;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;
using MqttProbe.Models.Mqtt;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Sparkplug;
using MqttProbe.Services.Telemetry;

namespace MqttProbe.Services.Mqtt;

public interface IMessageStoreManager : IDisposable
{
    public ConcurrentDictionary<string, MessageStore> MessageStores { get; }
    public MessageStore? SelectedMessageStore { get; set; }
    public bool IsListening { get; }
    public int MaxStoredMessages { get; }
    public Task<IEnumerable<MqttMessage>> GetMessagesForSelectedTopic();
    public Task ClearAllMessages();
    public Task Stop();
    public Task Start();

    public event Func<MqttMessage, Task>? MessageReceived;
}

public class MessageStoreManager : IMessageStoreManager
{
    private const int MaxTopicNodes = 10_000;

    private readonly IManagedMqttClient _client;
    private readonly ILogger<MessageStoreManager> _logger;
    private readonly ISettingsStore _settingsStore;
    private readonly IUxTelemetryService _telemetry;
    private readonly object _rateLimiterSync = new();
    private FixedWindowRateLimiter _rateLimiter;

    private int _totalNodeCount;
    private bool _nodeLimitLogged;
    private bool _rateLimitLogged;
    private long _droppedMessageCount;
    private readonly Lock _storeSync = new();
    private readonly Lock _lifecycleSync = new();
    private readonly Queue<MessageStore> _retentionOrder = new();
    private int _totalMessageCount;

    public MessageStoreManager(IManagedMqttClient client, ILogger<MessageStoreManager> logger,
        ISettingsStore settingsStore, IUxTelemetryService telemetry)
    {
        _client = client;
        _logger = logger;
        _settingsStore = settingsStore;
        _telemetry = telemetry;
        _rateLimiter = BuildRateLimiter(settingsStore.Config.Performance.MaxMessagesPerSecond);
        settingsStore.PerformanceSettingsChanged += OnPerformanceSettingsChanged;
    }

    public int MaxStoredMessages => _settingsStore.Config.Performance.MaxStoredMessages;

    public int TotalStoredMessages
    {
        get { lock (_storeSync) { return _totalMessageCount; } }
    }

    private static FixedWindowRateLimiter BuildRateLimiter(int permitsPerSecond) =>
        new(new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitsPerSecond,
            Window = TimeSpan.FromSeconds(1),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0
        });

    private void OnPerformanceSettingsChanged()
    {
        if (_disposed) return;
        var newLimiter = BuildRateLimiter(_settingsStore.Config.Performance.MaxMessagesPerSecond);
        FixedWindowRateLimiter oldLimiter;
        lock (_rateLimiterSync) { oldLimiter = _rateLimiter; _rateLimiter = newLimiter; }
        try { oldLimiter.Dispose(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to dispose old rate limiter; new limiter is already in place."); }
    }

    public MessageStore? SelectedMessageStore { get; set; }
    public ConcurrentDictionary<string, MessageStore> MessageStores { get; } = new();
    public bool IsListening { get; private set; }

    public event Func<MqttMessage, Task>? MessageReceived;

    private bool _disposed;

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing)
        {
            Stop().GetAwaiter().GetResult();
            _settingsStore.PerformanceSettingsChanged -= OnPerformanceSettingsChanged;
            _rateLimiter.Dispose();
        }
        _disposed = true;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public Task Start()
    {
        lock (_lifecycleSync)
        {
            if (IsListening) return Task.CompletedTask;
            _client.ApplicationMessageReceivedAsync += MessageHandler;
            IsListening = true;
        }

        return Task.CompletedTask;
    }

    public Task Stop()
    {
        lock (_lifecycleSync)
        {
            if (!IsListening) return Task.CompletedTask;
            _client.ApplicationMessageReceivedAsync -= MessageHandler;
            IsListening = false;
        }

        return Task.CompletedTask;
    }

    public Task<IEnumerable<MqttMessage>> GetMessagesForSelectedTopic()
    {
        lock (_storeSync)
        {
            if (SelectedMessageStore == null)
                return Task.FromResult<IEnumerable<MqttMessage>>([]);

            var list = new List<MqttMessage>();
            CollectMessages(SelectedMessageStore, list);
            list.Sort(static (a, b) => b.DateTimeReceived.CompareTo(a.DateTimeReceived));
            return Task.FromResult<IEnumerable<MqttMessage>>(list);
        }
    }

    public Task ClearAllMessages()
    {
        lock (_storeSync)
        {
            MessageStores.Clear();
            SelectedMessageStore = null;
            _totalNodeCount = 0;
            _totalMessageCount = 0;
            _retentionOrder.Clear();
            _nodeLimitLogged = false;
            _rateLimitLogged = false;
            Interlocked.Exchange(ref _droppedMessageCount, 0);
        }

        return Task.CompletedTask;
    }

    private static void CollectMessages(MessageStore? store, List<MqttMessage> result)
    {
        if (store == null) return;

        if (store.Messages is { IsEmpty: false })
            foreach (var msg in store.Messages)
                result.Add(msg);

        if (store.SubTopics == null) return;
        foreach (var sub in store.SubTopics.Values)
            CollectMessages(sub, result);
    }

    private void AddData(string fullTopic, int startIndex, MessageStore parent, MqttMessage message)
    {
        var nextSlash = fullTopic.IndexOf('/', startIndex);
        var hasChildren = nextSlash >= 0;
        var segEnd = hasChildren ? nextSlash : fullTopic.Length;
        var levelKey = fullTopic.Substring(startIndex, segEnd - startIndex);

        MessageStore? child = null;
        parent.SubTopics?.TryGetValue(levelKey, out child);
        if (child == null)
        {
            if (_totalNodeCount >= MaxTopicNodes)
            {
                LogNodeLimit();
                return;
            }

            var fullPath = fullTopic[..segEnd];
            var candidate = new MessageStore { Topic = levelKey, FullTopic = fullPath };
            var subTopics = parent.SubTopics ??= new ConcurrentDictionary<string, MessageStore>();
            child = subTopics.GetOrAdd(levelKey, candidate);
            if (ReferenceEquals(child, candidate))
                Interlocked.Increment(ref _totalNodeCount);
        }

        if (hasChildren)
            AddData(fullTopic, nextSlash + 1, child, message);
        else
            StoreMessage(child, message);
    }

    private void StoreMessage(MessageStore store, MqttMessage message)
    {
        store.Messages ??= new ConcurrentQueue<MqttMessage>();
        store.Messages.Enqueue(message);
        _retentionOrder.Enqueue(store);
        _totalMessageCount++;
        TrimToLimit();
    }

    // Evicts the globally-oldest message(s) until the total is within MaxStoredMessages.
    // Called only while holding _storeSync. The node at the head of _retentionOrder owns
    // the oldest message overall (per-topic queues and the global index share arrival order),
    // so dequeuing that node's queue removes exactly the right message — O(1), no scanning.
    private void TrimToLimit()
    {
        var limit = MaxStoredMessages;
        while (_totalMessageCount > limit && _retentionOrder.Count > 0)
        {
            var oldest = _retentionOrder.Dequeue();
            oldest.Messages?.TryDequeue(out _);
            _totalMessageCount--;
        }
    }

    private async Task MessageHandler(MqttApplicationMessageReceivedEventArgs arg)
    {
        FixedWindowRateLimiter limiter;
        lock (_rateLimiterSync) { limiter = _rateLimiter; }
        using var lease = limiter.AttemptAcquire();
        if (!lease.IsAcquired)
        {
            Interlocked.Increment(ref _droppedMessageCount);
            _telemetry.RecordMessageDropped();
            TryLogRateLimit();
            return;
        }
        _rateLimitLogged = false;

        var payloadSize = arg.ApplicationMessage.PayloadSegment.Count;
        _telemetry.RecordPayloadSize(payloadSize);

        var sw = Stopwatch.StartNew();
        MqttMessage? message = null;
        DecodedPayload? decodedPayload = null;

        try
        {
            var topic = arg.ApplicationMessage.Topic;
            var firstSlash = topic.IndexOf('/');
            var rootKey = firstSlash >= 0 ? topic[..firstSlash] : topic;

            decodedPayload = PayloadDecoder.Decode(arg);
            message = new MqttMessage(decodedPayload.Payload, topic,
                arg.ApplicationMessage.Retain, arg.ApplicationMessage.QualityOfServiceLevel);

            lock (_storeSync)
            {
                if (!MessageStores.TryGetValue(rootKey, out var messageStore))
                {
                    if (_totalNodeCount >= MaxTopicNodes)
                    {
                        LogNodeLimit();
                        return;
                    }

                    var candidate = new MessageStore { Topic = rootKey, FullTopic = rootKey };
                    messageStore = MessageStores.GetOrAdd(rootKey, candidate);
                    if (ReferenceEquals(messageStore, candidate))
                        Interlocked.Increment(ref _totalNodeCount);
                }

                if (firstSlash >= 0)
                    AddData(topic, firstSlash + 1, messageStore, message);
                else
                    StoreMessage(messageStore, message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message on topic {Topic}: {Message}", arg.ApplicationMessage.Topic, ex.Message);
        }

        sw.Stop();
        _telemetry.RecordProcessingTime(sw.Elapsed.TotalMicroseconds);
        if (decodedPayload is not null)
            _telemetry.RecordMessageProcessed(decodedPayload.Format.ToString());

        if (message != null && MessageReceived is { } handler)
        {
            try
            {
                await handler(message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MessageReceived handler threw on topic {Topic}", message.Topic);
            }
        }
    }

    private void TryLogRateLimit()
    {
        if (_rateLimitLogged) return;
        _rateLimitLogged = true;
        _logger.LogWarning(
            "Inbound message rate limit ({Limit}/s) exceeded; messages are being dropped. " +
            "Increase MaxMessagesPerSecond in PerformanceSettings if needed.",
            _settingsStore.Config.Performance.MaxMessagesPerSecond);
    }

    private void LogNodeLimit()
    {
        if (_nodeLimitLogged) return;
        _nodeLimitLogged = true;
        _logger.LogWarning("Topic-tree node limit ({Limit}) reached; new topics will be dropped.", MaxTopicNodes);
    }
}
