using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Logging;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;
using MqttProbe.Models.Mqtt;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Metrics;
using MqttProbe.Services.Plugins.Pipeline;
using MqttProbe.Services.Sparkplug;

namespace MqttProbe.Services.Mqtt;

public interface IMessageStoreManager : IDisposable
{
    public ConcurrentDictionary<string, MessageStore> MessageStores { get; }
    public MessageStore? SelectedMessageStore { get; set; }
    public bool IsListening { get; }
    public int MaxStoredMessages { get; }
    public int MaxTopicNodes { get; }
    public int TotalStoredMessages { get; }
    public int TopicNodeCount { get; }
    public long DroppedMessageCount { get; }
    public Task<IEnumerable<MqttMessage>> GetMessagesForSelectedTopic();
    public Task<IReadOnlyList<MqttMessage>> GetRecentMessagesAsync(string topic, int limit);
    public long GetVersion();
    public long GetSelectedTopicVersion();
    public Task ClearAllMessages();
    public Task Stop();
    public Task Start();

    public event Func<MqttMessage, Task>? MessageReceived;
}

public class MessageStoreManager : IMessageStoreManager
{
    public int MaxTopicNodes => _settingsStore.Config.Performance.MaxTopicNodes;

    private readonly IManagedMqttClient _client;
    private readonly ILogger<MessageStoreManager> _logger;
    private readonly ISettingsStore _settingsStore;
    private readonly IUxMetricsService _metrics;
    private readonly PayloadPipeline _pipeline;
    private readonly ISparkplugTopologyService? _topologyService;
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
    private long _globalVersion;
    private long _selectedTopicVersion;

    public MessageStoreManager(IManagedMqttClient client, ILogger<MessageStoreManager> logger,
        ISettingsStore settingsStore, IUxMetricsService metrics,
        PayloadPipeline pipeline, ISparkplugTopologyService? topologyService = null)
    {
        _client = client;
        _logger = logger;
        _settingsStore = settingsStore;
        _metrics = metrics;
        _pipeline = pipeline;
        _topologyService = topologyService;
        _rateLimiter = BuildRateLimiter(settingsStore.Config.Performance.MaxMessagesPerSecond);
        settingsStore.PerformanceSettingsChanged += OnPerformanceSettingsChanged;
    }

    public int MaxStoredMessages => _settingsStore.Config.Performance.MaxStoredMessages;

    public int TotalStoredMessages
    {
        get { lock (_storeSync) { return _totalMessageCount; } }
    }

    public int TopicNodeCount => Volatile.Read(ref _totalNodeCount);

    public long DroppedMessageCount => Interlocked.Read(ref _droppedMessageCount);

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

        lock (_storeSync) { TrimToLimit(); }
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

    public Task<IReadOnlyList<MqttMessage>> GetRecentMessagesAsync(string topic, int limit)
    {
        lock (_storeSync)
        {
            var node = FindNode(topic);
            if (node is null)
                return Task.FromResult<IReadOnlyList<MqttMessage>>([]);

            var collected = new List<MqttMessage>();
            CollectMessages(node, collected);
            if (collected.Count == 0)
                return Task.FromResult<IReadOnlyList<MqttMessage>>([]);

            collected.Sort(static (a, b) => b.DateTimeReceived.CompareTo(a.DateTimeReceived));

            return collected.Count <= limit
                ? Task.FromResult<IReadOnlyList<MqttMessage>>(collected)
                : Task.FromResult<IReadOnlyList<MqttMessage>>(collected.GetRange(0, limit));
        }
    }

    public long GetVersion() => Interlocked.Read(ref _globalVersion);

    public long GetSelectedTopicVersion() => Interlocked.Read(ref _selectedTopicVersion);

    private MessageStore? FindNode(string fullTopic)
    {
        var segments = fullTopic.Split('/');
        if (segments.Length == 0) return null;

        if (!MessageStores.TryGetValue(segments[0], out var current))
            return null;

        for (var i = 1; i < segments.Length; i++)
        {
            if (current.SubTopics is null || !current.SubTopics.TryGetValue(segments[i], out current))
                return null;
        }

        return current;
    }

    private static void IncrementTopicCounts(MessageStore parent)
    {
        for (var node = parent; node != null; node = node.Parent)
            node.TopicCount++;
    }

    private static void IncrementMessageCounts(MessageStore leaf)
    {
        for (var node = leaf; node != null; node = node.Parent)
            node.MessageCount++;
    }

    private static void DecrementMessageCounts(MessageStore leaf)
    {
        for (var node = leaf; node != null; node = node.Parent)
            node.MessageCount--;
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
            Interlocked.Increment(ref _globalVersion);
            Interlocked.Increment(ref _selectedTopicVersion);
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

    private bool IsSelectedTopicOrDescendant(MessageStore store)
    {
        var selected = SelectedMessageStore;
        if (selected is null) return false;
        if (ReferenceEquals(store, selected)) return true;

        var selectedPath = selected.FullTopic;
        var storePath = store.FullTopic;
        if (selectedPath is null || storePath is null) return false;

        if (storePath.StartsWith(selectedPath, StringComparison.Ordinal)
            && storePath.Length > selectedPath.Length
            && storePath[selectedPath.Length] == '/')
            return true;

        if (selectedPath.StartsWith(storePath, StringComparison.Ordinal)
            && selectedPath.Length > storePath.Length
            && selectedPath[storePath.Length] == '/')
            return true;

        return false;
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
            var candidate = new MessageStore { Topic = levelKey, FullTopic = fullPath, Parent = parent };
            var subTopics = parent.SubTopics ??= new ConcurrentDictionary<string, MessageStore>();
            child = subTopics.GetOrAdd(levelKey, candidate);
            if (ReferenceEquals(child, candidate))
            {
                Interlocked.Increment(ref _totalNodeCount);
                IncrementTopicCounts(parent);
            }
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
        IncrementMessageCounts(store);
        Interlocked.Increment(ref _globalVersion);
        if (IsSelectedTopicOrDescendant(store))
            Interlocked.Increment(ref _selectedTopicVersion);
        TrimToLimit();
    }

    private void TrimToLimit()
    {
        var limit = MaxStoredMessages;
        while (_totalMessageCount > limit && _retentionOrder.Count > 0)
        {
            var oldest = _retentionOrder.Dequeue();
            oldest.Messages?.TryDequeue(out _);
            _totalMessageCount--;
            DecrementMessageCounts(oldest);
            Interlocked.Increment(ref _globalVersion);
            if (IsSelectedTopicOrDescendant(oldest))
                Interlocked.Increment(ref _selectedTopicVersion);
        }
    }

    internal static string? Normalize(string fullTopic)
    {
        var trimmed = fullTopic.Trim('/');
        if (trimmed.Length == 0) return null;

        var segments = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('/', segments);
    }

    internal void AddMessage(string fullTopic, MqttMessage message)
    {
        lock (_storeSync)
        {
            var normalized = Normalize(fullTopic);
            if (normalized is null) return;

            var firstSlash = normalized.IndexOf('/');
            var rootKey = firstSlash >= 0 ? normalized[..firstSlash] : normalized;

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
                AddData(normalized, firstSlash + 1, messageStore, message);
            else
                StoreMessage(messageStore, message);
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
            _metrics.RecordMessageDropped();
            TryLogRateLimit();
            return;
        }
        _rateLimitLogged = false;

        var payloadSize = arg.ApplicationMessage.PayloadSegment.Count;
        _metrics.RecordPayloadSize(payloadSize);

        var sw = Stopwatch.StartNew();
        MqttMessage? message = null;
        string? formatId = null;

        try
        {
            var topic = arg.ApplicationMessage.Topic;
            var result = _pipeline.ProcessInbound(arg);
            var payloadText = result.Envelope.DisplayText;
            formatId = result.Envelope.FormatId;

            if (result.Diagnostics.Count > 0)
            {
                foreach (var diag in result.Diagnostics)
                    _logger.LogWarning("Pipeline diagnostic on topic {Topic}: {Diagnostic}", topic, diag);
            }

            if (_topologyService is not null && result.TopologyEvents.Count > 0)
                _topologyService.ApplyTopologyEvents(result.TopologyEvents);

            IReadOnlyDictionary<ulong, string>? aliasNames = null;
            if (formatId == "sparkplug-b"
                && !result.Envelope.IsFailure
                && _settingsStore.Config.Ui.EnrichSparkplugAliasNames
                && _topologyService is not null)
            {
                var rawPayload = arg.ApplicationMessage.PayloadSegment.Count > 0
                    ? arg.ApplicationMessage.PayloadSegment.ToArray()
                    : [];
                aliasNames = SparkplugAliasResolver.Resolve(topic, rawPayload, _topologyService.Groups);
            }

            message = new MqttMessage(payloadText, topic,
                arg.ApplicationMessage.Retain, arg.ApplicationMessage.QualityOfServiceLevel)
            {
                AliasNames = aliasNames
            };

            AddMessage(topic, message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message on topic {Topic}: {Message}", arg.ApplicationMessage.Topic, ex.Message);
        }

        sw.Stop();
        _metrics.RecordProcessingTime(sw.Elapsed.TotalMicroseconds);
        if (formatId is not null)
            _metrics.RecordMessageProcessed(formatId);

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
