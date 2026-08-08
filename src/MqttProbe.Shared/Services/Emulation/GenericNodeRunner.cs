using Microsoft.Extensions.Logging;
using MQTTnet;
using MqttProbe.Models.Emulation;
using MqttProbe.Services.Mqtt;
using MqttProbe.Services.Plugins.Contracts;
using MqttProbe.Services.Plugins.Pipeline;
using SparkplugNet.VersionB.Data;

namespace MqttProbe.Services.Emulation;

public class GenericNodeRunner(EmulatorNodeConfig config, IMqttManagedClient managedMqttClient, PayloadPipeline pipeline, ILogger logger) : INodeRunner
{
    private readonly Dictionary<Guid, WaveformState> _states = [];

    public Guid NodeId => config.Id;

    public NodeRuntimeStatus Status { get; private set; } = NodeRuntimeStatus.Idle;

    public Task StartAsync()
    {
        Status = NodeRuntimeStatus.Connected;
        return Task.CompletedTask;
    }

    public async Task PublishTickAsync(double tSeconds, IReadOnlyList<Metric> nodeHealthMetrics)
    {
        try
        {
            foreach (var device in config.Devices.Where(d => d.Metrics.Count > 0))
            {
                if (config.PayloadFormatId == "json")
                    await PublishDeviceBundleAsync(device, tSeconds);
                else
                    await PublishMetricsIndividuallyAsync(device, tSeconds);
            }
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex,
                "No encoder found for format '{FormatId}' on node '{NodeId}'; skipping publish for this tick",
                config.PayloadFormatId, config.NodeId);
        }
    }

    public Task StopAsync()
    {
        Status = NodeRuntimeStatus.Idle;
        return Task.CompletedTask;
    }

    private Task PublishDeviceBundleAsync(EmulatorDeviceConfig device, double tSeconds)
    {
        var metrics = new Dictionary<string, object>(device.Metrics.Count, StringComparer.Ordinal);
        foreach (var metric in device.Metrics)
        {
            metrics[metric.Name] = Sample(metric, tSeconds);
        }

        return PublishAsync(TopicTemplateRenderer.RenderDeviceTopic(config, device.DeviceId), metrics);
    }

    private async Task PublishMetricsIndividuallyAsync(EmulatorDeviceConfig device, double tSeconds)
    {
        foreach (var metric in device.Metrics)
        {
            await PublishAsync(
                TopicTemplateRenderer.RenderMetricTopic(config, device.DeviceId, metric.Name),
                new Dictionary<string, object>(StringComparer.Ordinal) { [metric.Name] = Sample(metric, tSeconds) });
        }
    }

    private Task PublishAsync(string topic, Dictionary<string, object> metrics)
    {
        var request = new PayloadEncoderRequest
        {
            Topic = topic,
            FormatId = config.PayloadFormatId,
            Metrics = metrics,
            TimestampUtc = DateTime.UtcNow
        };

        return managedMqttClient.EnqueueAsync(new MqttApplicationMessageBuilder()
            .WithTopic(request.Topic)
            .WithPayload(pipeline.EncodeOutbound(request))
            .Build());
    }

    private object Sample(EmulatorMetricConfig metric, double tSeconds)
    {
        var value = WaveformSampler.Next(metric, State(metric), tSeconds);
        return metric.ValueType switch
        {
            MetricValueType.Boolean => value >= 0.5,
            MetricValueType.Int64 => (long)Math.Round(value),
            _ => value
        };
    }

    private WaveformState State(EmulatorMetricConfig metric)
    {
        if (!_states.TryGetValue(metric.Id, out var state))
        {
            state = WaveformSampler.CreateState(metric);
            _states[metric.Id] = state;
        }

        return state;
    }
}
