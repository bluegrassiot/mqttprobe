using MQTTnet;
using MQTTnet.Packets;

namespace MqttProbe.Services.Mqtt;

public interface IMqttManagedClient : IAsyncDisposable, IDisposable
{
    public bool IsConnected { get; }
    public bool IsStarted { get; }

    public Task StartAsync(MqttManagedClientOptions options, CancellationToken cancellationToken = default);
    public Task StopAsync(CancellationToken cancellationToken = default);

    public Task SubscribeAsync(IEnumerable<MqttTopicFilter> topicFilters, CancellationToken cancellationToken = default);
    public Task UnsubscribeAsync(IEnumerable<string> topics, CancellationToken cancellationToken = default);

    public Task EnqueueAsync(MqttApplicationMessage applicationMessage, CancellationToken cancellationToken = default);

    public event Func<MqttClientConnectedEventArgs, Task>? ConnectedAsync;
    public event Func<MqttClientDisconnectedEventArgs, Task>? DisconnectedAsync;
    public event Func<MqttConnectingFailedEventArgs, Task>? ConnectingFailedAsync;
    public event Func<EventArgs, Task>? ConnectionStateChangedAsync;
    public event Func<MqttApplicationMessageReceivedEventArgs, Task>? ApplicationMessageReceivedAsync;
    public event Func<MqttManagedProcessFailedEventArgs, Task>? SynchronizingSubscriptionsFailedAsync;
}
