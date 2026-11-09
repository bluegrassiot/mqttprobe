using System.Collections.Immutable;
using MqttProbe.Core.Models.Mqtt;

namespace MqttProbe.UI.Components.Browser;

public partial class PayloadBrowser
{
    // A scoped clear can leave the selection in place while removing the open message. The list
    // heading then names the selection, not one of its topics. A metric filter names its owner
    // and exact metric even when no stored message matches it.
    private string? SelectionHeading
    {
        get
        {
            var state = MessageStoreManager.GetSelectedTopicState();
            return state.Mode switch
            {
                TopicSelectionMode.MultipleTopics => state.Label,
                TopicSelectionMode.SparkplugMetric => state.Label ?? _messageList.FirstOrDefault()?.Topic,
                _ => _messageList.FirstOrDefault()?.Topic
            };
        }
    }

    private string? MetricFilterLabel()
    {
        var state = MessageStoreManager.GetSelectedTopicState();
        return state.Mode == TopicSelectionMode.SparkplugMetric ? state.Label : null;
    }

    private ImmutableArray<MqttSparkplugMetricTarget> MetricTargets()
    {
        var state = MessageStoreManager.GetSelectedTopicState();
        return state.Mode == TopicSelectionMode.SparkplugMetric && !state.Metrics.IsDefaultOrEmpty
            ? state.Metrics
            : [];
    }

    private void ClearMetricFilter()
    {
        if (MessageStoreManager.GetSelectedTopicState().Mode != TopicSelectionMode.SparkplugMetric)
            return;

        MessageStoreManager.SelectTopics(null);
        ClearIfNoSelection();
        StateHasChanged();
    }
}
