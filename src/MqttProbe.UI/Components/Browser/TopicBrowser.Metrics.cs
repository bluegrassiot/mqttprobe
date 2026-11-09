using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Models.Sparkplug;
using MqttProbe.Core.Services.Sparkplug;
using MqttProbe.UI.Components.Sparkplug;

namespace MqttProbe.UI.Components.Browser;

public partial class TopicBrowser
{
    private Func<SparkplugSelectionScope, IReadOnlyList<SpbMetricSnapshot>>? _metricSource;
    private bool _topologySubscribed;

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

    // Topology can change without the topic tree version moving, so this path bypasses the
    // refresh cursor instead of waiting for a tree change that may never come.
    private void OnTopologyChanged() => _ = RefreshFromTopologyAsync();

    private async Task RefreshFromTopologyAsync()
    {
        try
        {
            await InvokeAsync(() =>
            {
                if (_disposed) return;

                ApplyFrame(RefreshFrame(force: true));
                StateHasChanged();
            });
        }
        catch (ObjectDisposedException) when (_disposed) { /* component torn down mid-refresh; nothing left to render */ }
        catch (Exception ex) { Logger.LogWarning(ex, "TopicBrowser topology refresh render failed"); }
    }

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
