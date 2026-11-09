using Microsoft.Extensions.Logging;
using MqttProbe.Core.Models.Mqtt;

namespace MqttProbe.Core.Services.Mqtt;

public partial class MessageStoreManager
{
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

    private void OnPurgeExcludedSparkplug(SparkplugTopicExclude rule)
    {
        bool Predicate(string topic) =>
            TopicBranchMatcher.MatchesSparkplug(rule.Scope, rule.MessageType, topic);

        _store.RemoveMatchingTopics(Predicate);
        // ReSharper disable once InconsistentlySynchronizedField
        if (_topologyService?.RemoveMatchingTopicsSilent(Predicate) > 0)
            Interlocked.Exchange(ref _topologyChangedPending, 1);
    }

    // Observer phase: no locks and no semaphore held, so a TopologyChanged subscriber may
    // re-enter the exclude service.
    private void OnTopicExcluded(string filter) => FlushTopologyChanged();

    private void OnSparkplugExcluded(SparkplugTopicExclude rule) => FlushTopologyChanged();

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
}
