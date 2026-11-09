using Microsoft.AspNetCore.Components;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Models.Sparkplug;
using MqttProbe.Core.Services.Sparkplug;
using MqttProbe.UI.Components.Sparkplug;

namespace MqttProbe.UI.Components.Browser;

public partial class TopicBrowser
{
    private Func<SparkplugSelectionScope, IReadOnlyList<SpbMetricSnapshot>>? _metricSource;
    private bool _topologySubscribed;
    private int _topologyDirty;

    [Parameter] public ISparkplugTopologyService? TopologyService { get; set; }

    [Parameter] public EventCallback<SparkplugChartSelection> OnAddMetricToChart { get; set; }

    private MetricProjection? MetricRows =>
        _metricSource is null ? null : new MetricProjection(_metricSource);

    private void InitializeMetricSource()
    {
        if (TopologyService is null)
            return;

        _metricSource = ScopeMetrics;
        TopologyService.TopologyChanged += OnTopologyChanged;
        _topologySubscribed = true;
    }

    private void ReleaseMetricSource()
    {
        if (_topologySubscribed && TopologyService is { } topology)
            topology.TopologyChanged -= OnTopologyChanged;

        _topologySubscribed = false;
    }

    private IReadOnlyList<SpbMetricSnapshot> ScopeMetrics(SparkplugSelectionScope scope)
    {
        if (TopologyService is not { } topology)
            return [];

        var groupSeparator = scope.GroupPath.LastIndexOf('/');
        var groupId = groupSeparator < 0 ? scope.GroupPath : scope.GroupPath[(groupSeparator + 1)..];

        if (!topology.Groups.TryGetValue(groupId, out var group)
            || !group.Nodes.TryGetValue(scope.NodeId, out var node))
            return [];

        if (scope.Kind == SparkplugSelectionKind.Node)
            return node.Metrics;

        return scope.DeviceId is { } deviceId && node.Devices.TryGetValue(deviceId, out var device)
            ? device.Metrics
            : [];
    }

    // Broker threads only mark the tree dirty; the existing refresh timer owns the rebuild, so a
    // telemetry burst costs one projection instead of one per event.
    private void OnTopologyChanged() => Interlocked.Exchange(ref _topologyDirty, 1);

    // Takes the pending change atomically, so an event that lands during a refresh stays queued
    // for the next tick instead of being lost.
    private bool TakeTopologyDirty() => Interlocked.Exchange(ref _topologyDirty, 0) == 1;

    private EventCallback<TopicTreeRowModel> MetricChartCallback =>
        OnAddMetricToChart.HasDelegate
            ? EventCallback.Factory.Create<TopicTreeRowModel>(this, OnMetricChart)
            : default;

    private Task OnMetricChart(TopicTreeRowModel row)
    {
        if (!OnAddMetricToChart.HasDelegate
            || row.MetricName is not { } metric
            || row.MetricChartTopic is not { } topic)
            return Task.CompletedTask;

        return OnAddMetricToChart.InvokeAsync(new SparkplugChartSelection(
            topic, SparkplugChartTargets.JsonPath(metric), metric));
    }
}
