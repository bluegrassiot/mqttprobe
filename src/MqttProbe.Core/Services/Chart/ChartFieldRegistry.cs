using System.Collections.Concurrent;
using MqttProbe.Core.Models.Chart;
using MqttProbe.Core.Services.Mqtt;

namespace MqttProbe.Core.Services.Chart;

public interface IChartFieldRegistry
{
    public void Update(string topic, IReadOnlyDictionary<string, ExtractedField> fields);
    public IReadOnlyList<string> GetTopics();
    public IReadOnlyList<DiscoveredField> GetFields(string topic);
    public IReadOnlyList<DiscoveredField> GetAllFields();
    public bool RemoveMatchingTopic(string filter);
    public bool RemoveMatchingTopics(Func<string, bool> predicate);
}

public class ChartFieldRegistry : IChartFieldRegistry
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, DiscoveredField>> _registry = new(StringComparer.Ordinal);

    public void Update(string topic, IReadOnlyDictionary<string, ExtractedField> fields)
    {
        var topicFields = _registry.GetOrAdd(topic, static _ => new ConcurrentDictionary<string, DiscoveredField>(StringComparer.Ordinal));
        var now = DateTime.UtcNow;
        foreach (var (path, extracted) in fields)
        {
            topicFields.AddOrUpdate(
                path,
                addKey => new DiscoveredField
                {
                    Topic = topic,
                    JsonPath = addKey,
                    LastValue = extracted.Value,
                    LastSeen = now,
                    ContextJson = extracted.ContextJson
                },
                (_, existing) =>
                {
                    existing.LastValue = extracted.Value;
                    existing.LastSeen = now;
                    existing.ContextJson = extracted.ContextJson;
                    return existing;
                });
        }
    }

    public IReadOnlyList<string> GetTopics() =>
        [.. _registry.Keys.OrderBy(k => k, StringComparer.Ordinal)];

    public IReadOnlyList<DiscoveredField> GetFields(string topic) =>
        _registry.TryGetValue(topic, out var fields)
            ? [.. fields.Values.OrderBy(f => f.JsonPath, StringComparer.Ordinal)]
            : [];

    public IReadOnlyList<DiscoveredField> GetAllFields() =>
        [.. _registry.Values.SelectMany(d => d.Values)
            .OrderBy(f => f.Topic, StringComparer.Ordinal)
            .ThenBy(f => f.JsonPath, StringComparer.Ordinal)];

    public bool RemoveMatchingTopic(string filter) =>
        RemoveMatchingTopics(topic => TopicExcludeService.Matches(topic, filter));

    public bool RemoveMatchingTopics(Func<string, bool> predicate)
    {
        var removed = false;
        foreach (var topic in _registry.Keys)
        {
            if (!predicate(topic))
                continue;

            removed |= _registry.TryRemove(topic, out _);
        }

        return removed;
    }
}
