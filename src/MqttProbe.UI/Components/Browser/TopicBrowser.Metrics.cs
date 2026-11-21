using System.Collections.Immutable;
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

    private bool EffectiveMultiSelect => _multiSelect && !ForceSingleSelect;

    private ImmutableArray<MqttSparkplugMetricTarget> SelectedMetrics =>
        _singleSelection is { Mode: TopicSelectionMode.SparkplugMetric } selection
            ? selection.Metrics
            : ImmutableArray<MqttSparkplugMetricTarget>.Empty;

    private ImmutableArray<MqttSparkplugMetricTarget> CurrentSelectedMetrics()
    {
        var state = TopicSelectionStore.GetSelectedTopicState();
        return state.Mode == TopicSelectionMode.SparkplugMetric
            ? state.Metrics
            : ImmutableArray<MqttSparkplugMetricTarget>.Empty;
    }

    private static ImmutableArray<MqttSparkplugMetricTarget> MetricTargetsOf(TopicTreeRowModel row)
    {
        if (!row.MetricTargets.IsDefaultOrEmpty)
            return row.MetricTargets;

        if (row.MetricName is { } name && row.Scope is { } scope)
            return [new MqttSparkplugMetricTarget(scope, name)];

        return ImmutableArray<MqttSparkplugMetricTarget>.Empty;
    }

    private async Task SelectMetricRow(TopicTreeRowModel row)
    {
        var request = row.ToSelection();
        _bulkSelection.DiscardTargets();
        TopicSelectionStore.SelectTopics(request);
        BuildVisibleRows();
        StateHasChanged();

        // Shared checks must clear with the committed metric, before the external callback.
        _bulkSelection.NotifyChanged();

        var selection = TopicSelectionStore.GetSelectedTopicState();
        if (string.Equals(selection.Key, request.Key, StringComparison.Ordinal)
            && OnSelectedChanged.HasDelegate)
            await OnSelectedChanged.InvokeAsync(selection);
    }

    private async Task ToggleMetricTargets(ImmutableArray<MqttSparkplugMetricTarget> targets)
    {
        if (targets.IsDefaultOrEmpty)
            return;

        var selected = CurrentSelectedMetrics();
        var allChecked = targets.All(target => selected.Contains(target));
        var next = new List<MqttSparkplugMetricTarget>(selected);
        if (allChecked)
        {
            next.RemoveAll(targets.Contains);
        }
        else
        {
            next.AddRange(targets.Where(target => !next.Contains(target)));
        }

        _bulkSelection.DiscardTargets();
        TopicSelectionStore.SelectTopics(next.Count == 0 ? null : TopicSelection.ForSparkplugMetrics(next));
        BuildVisibleRows();
        StateHasChanged();

        _bulkSelection.NotifyChanged();

        var selection = TopicSelectionStore.GetSelectedTopicState();
        if (OnSelectedChanged.HasDelegate)
            await OnSelectedChanged.InvokeAsync(selection);
    }

    private TopicTreeSelectionState MetricSelectionState(ImmutableArray<MqttSparkplugMetricTarget> targets)
    {
        if (targets.IsDefaultOrEmpty)
            return TopicTreeSelectionState.Unchecked;

        var selected = SelectedMetrics;
        var checkedCount = targets.Count(target => selected.Contains(target));

        if (checkedCount == 0)
            return TopicTreeSelectionState.Unchecked;

        return checkedCount == targets.Length
            ? TopicTreeSelectionState.Checked
            : TopicTreeSelectionState.Mixed;
    }

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
