using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Metrics;
using MqttProbe.Core.Services.Plugins.Pipeline;
using MqttProbe.Core.Services.Sparkplug;
using MqttProbe.PluginContracts;

namespace MqttProbe.Core.Services.Mqtt;

public class MessageStoreManager : IMessageStoreManager
{
    private readonly IMqttManagedClient _client;
    private readonly ILogger<MessageStoreManager> _logger;
    private readonly IPerformanceSettings _performanceSettings;
    private readonly IUxMetricsService _metrics;
    private readonly PayloadPipeline _pipeline;
    private readonly ISparkplugTopologyService? _topologyService;
    private readonly ISparkplugCommandService? _commandService;
    private readonly TopicTreeStore _store;
    private readonly InboundRateLimiter _rateLimiter;
    private readonly SparkplugAliasEnricher _aliasEnricher;
    private readonly ITopicExcludeService? _topicExcludeService;
    private readonly Lock _lifecycleSync = new();
    private int _disposed;
    private int _topologyChangedPending;

    public MessageStoreManager(IMqttManagedClient client, ILogger<MessageStoreManager> logger,
        IPerformanceSettings performanceSettings, IUxMetricsService metrics,
        PayloadPipeline pipeline, ISparkplugSettings sparkplugSettings,
        ISparkplugTopologyService? topologyService = null,
        ISparkplugCommandService? commandService = null,
        ITopicExcludeService? topicExcludeService = null)
    {
        _client = client;
        _logger = logger;
        _performanceSettings = performanceSettings;
        _metrics = metrics;
        _pipeline = pipeline;
        _topologyService = topologyService;
        _commandService = commandService;
        _topicExcludeService = topicExcludeService;
        _store = new TopicTreeStore(performanceSettings, logger);
        _rateLimiter = new InboundRateLimiter(performanceSettings, metrics, logger);
        _aliasEnricher = new SparkplugAliasEnricher(sparkplugSettings, topologyService);
        performanceSettings.PerformanceSettingsChanged += OnPerformanceSettingsChanged;
        if (_topicExcludeService is not null)
        {
            _topicExcludeService.PurgeExcludedTopic += OnPurgeExcludedTopic;
            _topicExcludeService.TopicExcluded += OnTopicExcluded;
        }
    }

    public ConcurrentDictionary<string, MessageStore> MessageStores => _store.MessageStores;

    public MessageStore? SelectedMessageStore
    {
        get => _store.SelectedMessageStore;
        set => _store.SelectedMessageStore = value;
    }

    public bool IsListening { get; private set; }

    public int MaxStoredMessages => _store.MaxStoredMessages;

    public int MaxTopicNodes => _store.MaxTopicNodes;

    public int TotalStoredMessages => _store.TotalStoredMessages;

    public int TopicNodeCount => _store.TopicNodeCount;

    public long DroppedMessageCount => _rateLimiter.DroppedCount;

    public long ExcludedMessageCount => _metrics.GetSnapshot().MessagesExcluded;

    public event Func<MqttMessage, Task>? MessageReceived;

    private void OnPerformanceSettingsChanged()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _rateLimiter.Rebuild();
        _store.ApplyRetentionLimit();
    }

    [SuppressMessage("ReSharper", "InconsistentlySynchronizedField")]
    protected virtual void Dispose(bool disposing)
    {
        // Claim disposal before tearing anything down. Raising the flag last would leave a
        // window where a settings change passes the guard above and then reaches components
        // that are already disposed; both of those calls are also safe to lose the race to.
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (disposing)
        {
            Stop().GetAwaiter().GetResult();
            _performanceSettings.PerformanceSettingsChanged -= OnPerformanceSettingsChanged;
            if (_topicExcludeService is not null)
            {
                _topicExcludeService.PurgeExcludedTopic -= OnPurgeExcludedTopic;
                _topicExcludeService.TopicExcluded -= OnTopicExcluded;
            }

            // Never raise TopologyChanged from teardown. A purge can still be mid-flight,
            // and it holds the exclude service while its purgers run, so a subscriber that
            // re-enters the exclude service from a synchronous raise here would deadlock
            // against it. The scope that owns this manager is going away, so the pending
            // notification is dropped with it instead of being announced late.
            Interlocked.Exchange(ref _topologyChangedPending, 0);
            _rateLimiter.Dispose();
        }
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

    public Task<IEnumerable<MqttMessage>> GetMessagesForSelectedTopic() =>
        Task.FromResult(_store.GetMessagesForSelectedTopic());

    public Task<IReadOnlyList<MqttMessage>> GetRecentMessagesAsync(string topic, int limit) =>
        Task.FromResult(_store.GetRecentMessages(topic, limit));

    public long GetVersion() => _store.Version;

    public long GetSelectedTopicVersion() => _store.SelectedTopicVersion;

    public Task ClearAllMessages()
    {
        _store.Clear();
        _rateLimiter.Reset();
        return Task.CompletedTask;
    }

    internal void AddMessage(string fullTopic, MqttMessage message) => _store.Add(fullTopic, message);

    private Task MessageHandler(MqttApplicationMessageReceivedEventArgs arg)
    {
        var topic = arg.ApplicationMessage.Topic;
        IDisposable? exclusionGate = null;
        if (_topicExcludeService is { } excludes)
        {
            exclusionGate = excludes.TryEnter(topic);
            if (exclusionGate is null)
            {
                _metrics.RecordMessageExcluded();
                return Task.CompletedTask;
            }
        }

        if (!_rateLimiter.TryAcquire())
        {
            exclusionGate?.Dispose();
            return Task.CompletedTask;
        }

        return ProcessMessageAsync(arg, topic, exclusionGate);
    }

    private async Task ProcessMessageAsync(MqttApplicationMessageReceivedEventArgs arg, string topic,
        IDisposable? exclusionGate)
    {
        MqttMessage? message = null;
        string? formatId = null;
        try
        {
            var payloadSize = arg.ApplicationMessage.GetPayloadSegment().Count;
            _metrics.RecordPayloadSize(payloadSize);

            var sw = Stopwatch.StartNew();

            try
            {
                var result = _pipeline.ProcessInbound(arg);
                var payloadText = result.Envelope.DisplayText;
                formatId = result.Envelope.FormatId;

                LogPipelineDiagnostics(topic, result.Diagnostics);

                if (result.TopologyEvents.Count > 0)
                    await ApplyTopologyEventsAsync(result.TopologyEvents);

                var aliasNames = _aliasEnricher.Resolve(topic, arg, result);

                message = new MqttMessage(payloadText, topic,
                    arg.ApplicationMessage.Retain, arg.ApplicationMessage.QualityOfServiceLevel)
                {
                    AliasNames = aliasNames,
                    FormatId = formatId
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
        }
        finally
        {
            // Released before MessageReceived: a subscriber that awaits Add would wait on this gate.
            exclusionGate?.Dispose();
        }

        if (message != null)
        {
            await NotifyMessageReceivedAsync(message);
        }
    }

    private async Task ApplyTopologyEventsAsync(IReadOnlyList<TopologyEvent> events)
    {
        if (_topologyService is not null)
            await _topologyService.ApplyTopologyEventsAsync(events);

        if (_commandService is null)
            return;

        foreach (var evt in events)
        {
            if (evt is NodeDataEvent nde)
                await _commandService.RequestNodeRebirthIfNeededAsync(nde.GroupId, nde.NodeId);
            else if (evt is DeviceDataEvent dde)
                await _commandService.RequestNodeRebirthIfNeededAsync(dde.GroupId, dde.NodeId);
        }
    }

    // Trusted purger: runs while the exclude service holds its lock, so topology removal
    // must stay silent here. A purge running inline with Add/Remove also holds the operation
    // semaphore; a purge finished later from a reader release holds no semaphore at all.
    private void OnPurgeExcludedTopic(string filter)
    {
        _store.RemoveMatchingTopic(filter);
        // ReSharper disable once InconsistentlySynchronizedField
        if (_topologyService?.RemoveMatchingTopicSilent(filter) > 0)
            Interlocked.Exchange(ref _topologyChangedPending, 1);
    }

    // Observer phase: no locks and no semaphore held, so a TopologyChanged subscriber may
    // re-enter the exclude service.
    private void OnTopicExcluded(string filter) => FlushTopologyChanged();

    // Teardown never gets here: it drops its pending notification instead of raising
    // publicly while a purge may still be in flight.
    private void FlushTopologyChanged()
    {
        if (Interlocked.Exchange(ref _topologyChangedPending, 0) == 0)
            return;

        try
        {
            _topologyService?.RaiseTopologyChanged();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TopologyChanged subscriber failed after an excluded topic purge");
        }
    }

    private void LogPipelineDiagnostics(string topic, IReadOnlyList<string> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            _logger.LogWarning("Pipeline diagnostic on topic {Topic}: {Diagnostic}", topic, diagnostic);
        }
    }

    private async Task NotifyMessageReceivedAsync(MqttMessage message)
    {
        if (MessageReceived is not { } handler)
        {
            return;
        }

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
