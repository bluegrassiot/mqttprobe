using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Models.Sparkplug;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Metrics;
using MqttProbe.Core.Services.Plugins.Pipeline;
using MqttProbe.Core.Services.Plugins.Protobuf;
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
    private CancellationTokenSource? _sequenceMonitorCts;
    private Task _sequenceMonitorTask = Task.CompletedTask;
    private int _disposed;
    private int _topologyChangedPending;

    public MessageStoreManager(IMqttManagedClient client, ILogger<MessageStoreManager> logger,
        IPerformanceSettings performanceSettings, IUxMetricsService metrics,
        PayloadPipeline pipeline, ISparkplugSettings sparkplugSettings,
        ISparkplugTopologyService? topologyService = null,
        ISparkplugCommandService? commandService = null,
        ITopicExcludeService? topicExcludeService = null,
        ISubscriptionManager? subscriptionManager = null)
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

    public int RootTopicCount => _store.RootTopicCount;

    public TopicTreeSnapshot GetTopicTreeSnapshot() => _store.GetTopicTreeSnapshot();

    public SelectedTopicState GetSelectedTopicState() => _store.GetSelectedTopicState();

    public void SelectTopic(string? fullTopic) => _store.SelectTopic(fullTopic);

    public void SelectTopics(TopicSelection? selection) => _store.SelectTopics(selection);

    public bool TryClearSelection(long treeVersion, SelectedTopicState expectedSelection) =>
        _store.TryClearSelection(treeVersion, expectedSelection);

    public Task<SelectedMessagesSnapshot?> GetSelectedMessagesAsync(SelectedTopicToken expectedSelection, int limit) =>
        Task.FromResult(_store.GetSelectedMessages(expectedSelection, limit));

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
            _client.DisconnectedAsync += OnClientDisconnectedAsync;
            _sequenceMonitorCts = new CancellationTokenSource();
            var monitorToken = _sequenceMonitorCts.Token;
            _sequenceMonitorTask = Task.Run(() => MonitorSequenceTimeoutsAsync(monitorToken), CancellationToken.None);
            IsListening = true;
        }

        return Task.CompletedTask;
    }

    public async Task Stop()
    {
        CancellationTokenSource? monitorCts;
        Task monitorTask;
        lock (_lifecycleSync)
        {
            if (!IsListening) return;
            _client.ApplicationMessageReceivedAsync -= MessageHandler;
            _client.DisconnectedAsync -= OnClientDisconnectedAsync;
            monitorCts = _sequenceMonitorCts;
            monitorTask = _sequenceMonitorTask;
            _sequenceMonitorCts = null;
            IsListening = false;
        }

        if (monitorCts is not null)
        {
            await monitorCts.CancelAsync().ConfigureAwait(false);
            try
            {
                await monitorTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                _logger.LogDebug(ex, "Sequence diagnostics monitor was canceled");
            }
            finally
            {
                monitorCts.Dispose();
            }
        }
    }

    private async Task MonitorSequenceTimeoutsAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    _topologyService?.CheckSequenceTimeouts();
                    if (_commandService is not null && _topologyService is not null)
                    {
                        var subscriptionSnapshot = _client.LiveSubscriptionSnapshot;
                        foreach (var group in _topologyService.Groups.Values)
                        {
                            foreach (var node in group.Nodes.Values.Where(node =>
                                          node.Status == SpbNodeStatus.Online
                                          && node.Sequence.State == SpbSequenceState.GapDetected
                                          && HasCurrentCompleteSequenceSubscriptions(group.GroupId, node.NodeId, subscriptionSnapshot)))
                            {
                                long? expectedEpoch;
                                lock (node.SyncRoot)
                                    expectedEpoch = node.SequenceConnectionEpoch;
                                await _commandService.RequestNodeRebirthIfNeededAsync(
                                    group.GroupId, node.NodeId, expectedEpoch,
                                    subscriptionSnapshot?.Revision, cancellationToken).ConfigureAwait(false);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Sequence diagnostics monitor failed");
                }
            }
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Sequence diagnostics monitor stopped");
        }
    }

    private Task OnClientDisconnectedAsync(MqttClientDisconnectedEventArgs args)
    {
        _topologyService?.ResetSequenceTracking();
        return Task.CompletedTask;
    }

    public long GetVersion() => _store.Version;

    public Task ClearAllMessages()
    {
        _store.Clear();
        _rateLimiter.Reset();
        return Task.CompletedTask;
    }

    internal void AddMessage(string fullTopic, MqttMessage message) => _store.Add(fullTopic, message);

    private Task MessageHandler(MqttApplicationMessageReceivedEventArgs arg)
    {
        var connectionEpoch = _client.InboundConnectionEpoch ?? _client.ConnectionEpoch;
        var subscriptionSnapshot = _client.InboundSubscriptionSnapshot;
        var topic = arg.ApplicationMessage.Topic;
        IDisposable? exclusionGate = null;
        if (_topicExcludeService is { } excludes)
        {
            exclusionGate = excludes.TryEnter(topic);
            if (exclusionGate is null)
            {
                _metrics.RecordMessageExcluded();
                _topologyService?.MarkSequenceObservationIncomplete(topic);
                return Task.CompletedTask;
            }
        }

        if (!_rateLimiter.TryAcquire())
        {
            exclusionGate?.Dispose();
            _topologyService?.MarkSequenceObservationIncomplete(topic);
            return Task.CompletedTask;
        }

        return ProcessMessageAsync(arg, topic, exclusionGate, connectionEpoch, subscriptionSnapshot);
    }

    private async Task ProcessMessageAsync(MqttApplicationMessageReceivedEventArgs arg, string topic,
        IDisposable? exclusionGate, long? connectionEpoch, MqttSubscriptionSnapshot? subscriptionSnapshot)
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
                    await ApplyTopologyEventsAsync(result.TopologyEvents, connectionEpoch, subscriptionSnapshot).ConfigureAwait(false);

                var aliasNames = _aliasEnricher.Resolve(topic, result);

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

    private async Task ApplyTopologyEventsAsync(
        IReadOnlyList<TopologyEvent> events,
        long? connectionEpoch,
        MqttSubscriptionSnapshot? subscriptionSnapshot)
    {
        var target = events.Select(GetSequenceTarget).FirstOrDefault(item => item is not null);
        var isComplete = target is { } sequenceTarget
                         && subscriptionSnapshot is { } snapshot
                         && snapshot.ConnectionEpoch == connectionEpoch
                         && HasCompleteSequenceSubscriptions(sequenceTarget.GroupId, sequenceTarget.NodeId, snapshot);
        var observationContext = new SpbSequenceObservationContext(
            subscriptionSnapshot?.ConnectionEpoch ?? connectionEpoch ?? 0,
            subscriptionSnapshot?.Revision ?? -1,
            isComplete);

        if (_topologyService is not null)
        {
            await _topologyService.ApplyTopologyEventsAsync(events, observationContext).ConfigureAwait(false);
        }

        if (_commandService is null)
            return;

        foreach (var evt in events)
        {
            if (evt is NodeDataEvent nde && CanRequestSequenceRecovery(nde.GroupId, nde.NodeId, subscriptionSnapshot))
            {
                await _commandService.RequestNodeRebirthIfNeededAsync(
                    nde.GroupId, nde.NodeId, connectionEpoch,
                    subscriptionSnapshot?.Revision, CancellationToken.None).ConfigureAwait(false);
            }
            else if (evt is DeviceDataEvent dde && CanRequestSequenceRecovery(dde.GroupId, dde.NodeId, subscriptionSnapshot))
            {
                await _commandService.RequestNodeRebirthIfNeededAsync(
                    dde.GroupId, dde.NodeId, connectionEpoch,
                    subscriptionSnapshot?.Revision, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static (string GroupId, string NodeId)? GetSequenceTarget(TopologyEvent evt) => evt switch
    {
        NodeBirthEvent item => (item.GroupId, item.NodeId),
        NodeDataEvent item => (item.GroupId, item.NodeId),
        DeviceBirthEvent item => (item.GroupId, item.NodeId),
        DeviceDataEvent item => (item.GroupId, item.NodeId),
        DeviceDeathEvent item => (item.GroupId, item.NodeId),
        NodeDeathEvent item => (item.GroupId, item.NodeId),
        _ => null
    };

    private bool CanRequestSequenceRecovery(string groupId, string nodeId, MqttSubscriptionSnapshot? subscriptionSnapshot)
    {
        var topology = _topologyService;
        if (topology is null || !topology.Groups.TryGetValue(groupId, out var group))
            return true;
        if (!group.Nodes.TryGetValue(nodeId, out var node))
            return true;

        // Missing-birth recovery must not depend on sequence validity; only a gap on an
        // online node needs proven subscription coverage.
        if (node.Status != SpbNodeStatus.Online)
            return true;

        return node.Sequence.State != SpbSequenceState.GapDetected
            || HasCurrentCompleteSequenceSubscriptions(groupId, nodeId, subscriptionSnapshot);
    }

    private bool HasCurrentCompleteSequenceSubscriptions(
        string groupId, string nodeId, MqttSubscriptionSnapshot? subscriptionSnapshot)
    {
        if (subscriptionSnapshot is null || _client.LiveSubscriptionSnapshot is not { } live
            || live.ConnectionEpoch != subscriptionSnapshot.ConnectionEpoch
            || live.Revision != subscriptionSnapshot.Revision)
            return false;

        var node = _topologyService?.Groups.GetValueOrDefault(groupId)?.Nodes.GetValueOrDefault(nodeId);
        if (node is null)
            return false;
        lock (node.SyncRoot)
        {
            if (!node.SequenceObservationComplete
                || node.SequenceConnectionEpoch != subscriptionSnapshot.ConnectionEpoch
                || node.SequenceSubscriptionRevision != subscriptionSnapshot.Revision)
                return false;
        }

        return HasCompleteSequenceSubscriptions(groupId, nodeId, subscriptionSnapshot);
    }

    private static bool HasCompleteSequenceSubscriptions(
        string groupId, string nodeId, MqttSubscriptionSnapshot subscriptionSnapshot)
    {
        var subscriptions = subscriptionSnapshot.ConfirmedFilters;
        if (subscriptions.Count == 0)
            return false;

        var nodeVerbs = new[] { "NBIRTH", "NDATA" };
        var deviceVerbs = new[] { "DBIRTH", "DDATA", "DDEATH" };
        return nodeVerbs.All(verb => subscriptions.Any(subscription =>
                   MqttTopicMatcher.Matches($"spBv1.0/{groupId}/{verb}/{nodeId}", subscription)))
               && deviceVerbs.All(verb => subscriptions.Any(subscription =>
                   CoversArbitraryDevice(subscription, groupId, verb, nodeId)));
    }

    private static bool CoversArbitraryDevice(string filter, string groupId, string verb, string nodeId)
    {
        var segments = filter.Split('/');
        var deviceWildcard = segments.Length > 4 && segments[4] == "+";
        var broadTerminalWildcard = segments[^1] == "#"
                                    && Array.IndexOf(segments, "#") <= 4;
        return (deviceWildcard || broadTerminalWildcard)
               && MqttTopicMatcher.Matches($"spBv1.0/{groupId}/{verb}/{nodeId}/__sequence_probe__", filter);
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
