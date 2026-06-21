using System.Collections.Concurrent;

namespace MqttProbe.Models.Mqtt;

public class MessageStore
{
    public ConcurrentDictionary<string, MessageStore>? SubTopics { get; set; }
    public string? Topic { get; init; }
    public string? FullTopic { get; init; }
    public ConcurrentQueue<MqttMessage>? Messages { get; set; }
}
