using MqttProbe.Core.Models.Mqtt;

namespace MqttProbe.Core.Services.Mqtt;

internal sealed partial class TopicTreeStore
{
    private DateTime? _timingBufferStart;
    private DateTime? _timingPreviousReceived;
    private readonly Dictionary<string, DateTime> _timingPreviousByTopic = new(StringComparer.Ordinal);

    private void RecordTiming(MqttMessage message, string topic)
    {
        var received = message.DateTimeReceived;

        if (_timingBufferStart is { } start)
        {
            message.ElapsedSinceBufferStart = received - start;
        }
        else
        {
            _timingBufferStart = received;
            message.ElapsedSinceBufferStart = TimeSpan.Zero;
        }

        if (_timingPreviousReceived is { } previous)
            message.SincePreviousAllTopics = received - previous;

        _timingPreviousReceived = received;

        if (_timingPreviousByTopic.TryGetValue(topic, out var previousForTopic))
            message.SincePreviousSameTopic = received - previousForTopic;

        _timingPreviousByTopic[topic] = received;
    }

    private void ResetTimingIfBufferEmptied(int removed)
    {
        if (removed > 0 && _totalMessageCount == 0)
            ResetTiming();
    }

    private void ResetTiming()
    {
        _timingBufferStart = null;
        _timingPreviousReceived = null;
        _timingPreviousByTopic.Clear();
    }
}
