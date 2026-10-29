using MqttProbe.Core.Models.Mqtt;

namespace MqttProbe.Core.Services.Mqtt;

public interface IMessageStoreManager : IDisposable
{
    public int RootTopicCount { get; }
    public TopicTreeSnapshot GetTopicTreeSnapshot();
    public SelectedTopicState GetSelectedTopicState();
    public void SelectTopic(string? fullTopic);

    public void SelectTopics(TopicSelection? selection);
    public bool TryClearSelection(long treeVersion, SelectedTopicState expectedSelection);
    public Task<SelectedMessagesSnapshot?> GetSelectedMessagesAsync(SelectedTopicToken expectedSelection, int limit);
    public bool IsListening { get; }
    public int MaxStoredMessages { get; }
    public int MaxTopicNodes { get; }
    public int TotalStoredMessages { get; }
    public int TopicNodeCount { get; }
    public long DroppedMessageCount { get; }
    public long ExcludedMessageCount { get; }
    public long GetVersion();
    public Task ClearAllMessages();
    public Task<int> ClearMessagesForTopicsAsync(IReadOnlyList<string> fullTopics);

    // CA1716: "Stop" clashes with a VB keyword. This is public API surface
    // consumed by third-party plugins (MqttProbe.UI) — do not rename.
#pragma warning disable CA1716 // Identifiers should not match keywords
    public Task Stop();
#pragma warning restore CA1716
    public Task Start();

    public event Func<MqttMessage, Task>? MessageReceived;
}
