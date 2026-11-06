using System.Collections.Immutable;
using MqttProbe.Core.Models.Mqtt;

namespace MqttProbe.Core.Services.Mqtt;

public sealed partial class TopicExcludeService
{
    public async Task<TopicExcludeBatchResult> AddMany(IReadOnlyList<string> topics)
    {
        Connection boundConnection;
        long boundGeneration;
        lock (_sync)
        {
            boundConnection = _sessionState.SelectedConnection;
            boundGeneration = _connectionGeneration;
        }

        var results = new List<TopicExcludeAddResult>(topics.Count);
        foreach (var topic in topics)
        {
            var (outcome, feedback) = await AddCore(topic, boundConnection, boundGeneration);
            results.Add(new(topic, outcome, feedback));
        }

        return new TopicExcludeBatchResult(ImmutableArray.CreateRange(results));
    }
}
