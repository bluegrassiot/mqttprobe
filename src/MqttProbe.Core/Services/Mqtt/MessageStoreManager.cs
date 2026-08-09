using System.Collections.Concurrent;
using System.Diagnostics;
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
    private readonly Lock _lifecycleSync = new();
    private int _disposed;

    public MessageStoreManager(IMqttManagedClient client, ILogger<MessageStoreManager> logger,
        IPerformanceSettings performanceSettings, IUxMetricsService metrics,
        PayloadPipeline pipeline, ISparkplugSettings sparkplugSettings,
        ISparkplugTopologyService? topologyService = null,
        ISparkplugCommandService? commandService = null)
    {
        _client = client;
        _logger = logger;
        _performanceSettings = performanceSettings;
        _metrics = metrics;
        _pipeline = pipeline;
        _topologyService = topologyService;
        _commandService = commandService;
        _store = new TopicTreeStore(performanceSettings, logger);
        _rateLimiter = new InboundRateLimiter(performanceSettings, metrics, logger);
        _aliasEnricher = new SparkplugAliasEnricher(sparkplugSettings, topologyService);
        performanceSettings.PerformanceSettingsChanged += OnPerformanceSettingsChanged;
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

    public event Func<MqttMessage, Task>? MessageReceived;

    private void OnPerformanceSettingsChanged()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _rateLimiter.Rebuild();
        _store.ApplyRetentionLimit();
    }

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

    private async Task MessageHandler(MqttApplicationMessageReceivedEventArgs arg)
    {
        if (!_rateLimiter.TryAcquire()) return;

        var payloadSize = arg.ApplicationMessage.GetPayloadSegment().Count;
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

            LogPipelineDiagnostics(topic, result.Diagnostics);

            if (_topologyService is not null && result.TopologyEvents.Count > 0)
                await _topologyService.ApplyTopologyEventsAsync(result.TopologyEvents);

            if (_commandService is not null)
            {
                foreach (var evt in result.TopologyEvents)
                {
                    if (evt is NodeDataEvent nde)
                        await _commandService.RequestNodeRebirthIfNeededAsync(nde.GroupId, nde.NodeId);
                    else if (evt is DeviceDataEvent dde)
                        await _commandService.RequestNodeRebirthIfNeededAsync(dde.GroupId, dde.NodeId);
                }
            }

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

        if (message != null)
        {
            await NotifyMessageReceivedAsync(message);
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
