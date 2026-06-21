using Microsoft.Extensions.Logging;
using SparkplugNet.Core;
using SparkplugNet.Core.Enumerations;
using SparkplugNet.Core.Node;
using SparkplugNet.VersionB;
using SparkplugNet.VersionB.Data;

namespace MqttProbe.Services.Sparkplug;

public interface ISparkplugNode
{
    public bool IsConnected { get; }
    public event Func<SparkplugBase<Metric>.SparkplugEventArgs, Task>? Connected;
    public event Func<SparkplugBase<Metric>.SparkplugEventArgs, Task>? Disconnected;
    public Task Start(SparkplugNodeOptions options);
    public Task Stop();
    public Task PublishMetrics(List<Metric> metrics);
    public Task PublishNodeDeathMessage();
    public Task PublishDeviceBirthMessage(string deviceId, List<Metric> metrics);
    public Task PublishDeviceMetrics(string deviceId, List<Metric> metrics);
}

public interface ISparkplugNodeFactory
{
    public ISparkplugNode Create(List<Metric> knownMetrics, SparkplugSpecificationVersion version);
}

internal sealed class SparkplugNodeAdapter : ISparkplugNode
{
    private readonly SparkplugNode _node;
    private readonly ILogger? _logger;
    private readonly List<Metric> _knownMetrics;

    public SparkplugNodeAdapter(SparkplugNode node, List<Metric> knownMetrics, ILogger? logger = null)
    {
        _node = node;
        _knownMetrics = knownMetrics;
        _logger = logger;
        _node.NodeCommandReceived += OnNodeCommandReceived;
    }

    private async Task OnNodeCommandReceived(SparkplugNodeBase<Metric>.NodeCommandEventArgs args)
    {
        var hasRebirth = args.Metrics.Any(m =>
            m.Name == "Node Control/Rebirth"
            && m.Value is bool b && b);
        if (hasRebirth)
        {
            if (_logger?.IsEnabled(LogLevel.Information) == true)
                _logger.LogInformation("Rebirth command received for node {GroupId}/{NodeId}",
                    args.GroupIdentifier, args.EdgeNodeIdentifier);
            await _node.Rebirth(_knownMetrics);
        }
    }

    public bool IsConnected => _node.IsConnected;

    public event Func<SparkplugBase<Metric>.SparkplugEventArgs, Task>? Connected
    {
        add => _node.Connected += value;
        remove => _node.Connected -= value;
    }

    public event Func<SparkplugBase<Metric>.SparkplugEventArgs, Task>? Disconnected
    {
        add => _node.Disconnected += value;
        remove => _node.Disconnected -= value;
    }

    public Task Start(SparkplugNodeOptions options) => _node.Start(options);
    public Task Stop() => _node.Stop();
    public Task PublishMetrics(List<Metric> metrics) => _node.PublishMetrics(metrics);

    public Task PublishNodeDeathMessage()
    {
        var method = _node.GetType().GetMethod(
            "SendNodeDeathMessage",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance); // NOSONAR — SendNodeDeathMessage is not exposed publicly by SparkplugNet
        if (method is not null)
            return (Task)method.Invoke(_node, null)!;

        throw new InvalidOperationException(
            "SparkplugNet method 'SendNodeDeathMessage' was not found via reflection. " +
            "The method may have been renamed or removed in a SparkplugNet update — " +
            "update the reflection call in SparkplugNodeAdapter.PublishNodeDeathMessage.");
    }

    public Task PublishDeviceBirthMessage(string deviceId, List<Metric> metrics)
        => _node.PublishDeviceBirthMessage(metrics, deviceId);

    public Task PublishDeviceMetrics(string deviceId, List<Metric> metrics)
        => _node.PublishDeviceData(metrics, deviceId);
}

public class SparkplugNodeFactory(ILogger<SparkplugNodeFactory>? logger = null) : ISparkplugNodeFactory
{
    public ISparkplugNode Create(List<Metric> knownMetrics, SparkplugSpecificationVersion version)
        => new SparkplugNodeAdapter(new SparkplugNode(knownMetrics, version), knownMetrics, logger);
}
