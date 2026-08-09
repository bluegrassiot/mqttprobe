using SparkplugNet.VersionB.Data;

namespace MqttProbe.Core.Services.Emulation;

public enum NodeRuntimeStatus { Idle, Connecting, Connected, Error }

public interface INodeRunner
{
    public Guid NodeId { get; }
    public NodeRuntimeStatus Status { get; }
    public Task StartAsync();
    public Task PublishTickAsync(double tSeconds, IReadOnlyList<Metric> nodeHealthMetrics);
    public Task StopAsync();
}
