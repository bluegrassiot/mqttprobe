using System.Collections.Concurrent;
using MqttProbe.Models.Chart;

namespace MqttProbe.Services.Chart;

public interface IChartFieldRegistry
{
    public void Update(string topic, IReadOnlyDictionary<string, ExtractedField> fields);
    public IReadOnlyList<string> GetTopics();
    public IReadOnlyList<DiscoveredField> GetFields(string topic);
    public IReadOnlyList<DiscoveredField> GetAllFields();
}

public class ChartFieldRegistry : IChartFieldRegistry
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, DiscoveredField>> _registry = new();

    public void Update(string topic, IReadOnlyDictionary<string, ExtractedField> fields)
    {
        var topicFields = _registry.GetOrAdd(topic, static _ => new ConcurrentDictionary<string, DiscoveredField>());
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
}
