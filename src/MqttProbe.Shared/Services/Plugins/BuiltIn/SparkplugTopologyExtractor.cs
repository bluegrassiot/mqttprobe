using System.Collections.Concurrent;
using MqttProbe.Services.Plugins.Contracts;
using Org.Eclipse.Tahu.Protobuf;

namespace MqttProbe.Services.Plugins.BuiltIn;

public sealed class SparkplugTopologyExtractor : ITopologyExtractor
{
    // Key: "group/node" for node-scoped, "group/node/device" for device-scoped.
    private readonly ConcurrentDictionary<string, Dictionary<ulong, string>> _aliasMaps = new();

    public string FormatId => "sparkplug-b";

    public IReadOnlyList<TopologyEvent> Extract(DecodedPayloadEnvelope envelope)
    {
        if (envelope.FormatId != FormatId || envelope.IsFailure)
            return [];

        if (envelope.TypedPayload is not Payload payload)
            return [];

        if (!TryParseTopic(envelope.Topic, out var group, out var verb, out var node, out var device))
            return [];

        return verb switch
        {
            "NBIRTH" => HandleNBirth(envelope, payload, group, node),
            "NDEATH" => [new NodeDeathEvent
            {
                FormatId = FormatId,
                Topic = envelope.Topic,
                GroupId = group,
                NodeId = node
            }],
            "NDATA" => HandleNData(envelope, payload, group, node),
            "DBIRTH" when device != null => HandleDBirth(envelope, payload, group, node, device),
            "DDEATH" when device != null => [new DeviceDeathEvent
            {
                FormatId = FormatId,
                Topic = envelope.Topic,
                GroupId = group,
                NodeId = node,
                DeviceId = device
            }],
            "DDATA" when device != null => HandleDData(envelope, payload, group, node, device),
            _ => []
        };
    }

    private IReadOnlyList<TopologyEvent> HandleNBirth(
        DecodedPayloadEnvelope envelope, Payload payload, string group, string node)
    {
        var key = $"{group}/{node}";
        var aliasMap = new Dictionary<ulong, string>();

        var metrics = ExtractMetrics(payload);
        foreach (var metric in payload.Metrics)
        {
            if (!string.IsNullOrEmpty(metric.Name) && metric.Alias != 0)
                aliasMap[metric.Alias] = metric.Name;
        }

        _aliasMaps[key] = aliasMap;

        return [new NodeBirthEvent
        {
            FormatId = FormatId,
            Topic = envelope.Topic,
            GroupId = group,
            NodeId = node,
            Metrics = metrics
        }];
    }

    private IReadOnlyList<TopologyEvent> HandleNData(
        DecodedPayloadEnvelope envelope, Payload payload, string group, string node)
    {
        var key = $"{group}/{node}";
        _aliasMaps.TryGetValue(key, out var aliasMap);

        var metrics = ExtractMetricsWithAliasResolution(payload, aliasMap);

        return [new NodeDataEvent
        {
            FormatId = FormatId,
            Topic = envelope.Topic,
            GroupId = group,
            NodeId = node,
            Metrics = metrics
        }];
    }

    private IReadOnlyList<TopologyEvent> HandleDBirth(
        DecodedPayloadEnvelope envelope, Payload payload, string group, string node, string device)
    {
        var key = $"{group}/{node}/{device}";
        var aliasMap = new Dictionary<ulong, string>();

        var metrics = ExtractMetrics(payload);
        foreach (var metric in payload.Metrics)
        {
            if (!string.IsNullOrEmpty(metric.Name) && metric.Alias != 0)
                aliasMap[metric.Alias] = metric.Name;
        }

        _aliasMaps[key] = aliasMap;

        return [new DeviceBirthEvent
        {
            FormatId = FormatId,
            Topic = envelope.Topic,
            GroupId = group,
            NodeId = node,
            DeviceId = device,
            Metrics = metrics
        }];
    }

    private IReadOnlyList<TopologyEvent> HandleDData(
        DecodedPayloadEnvelope envelope, Payload payload, string group, string node, string device)
    {
        var key = $"{group}/{node}/{device}";
        _aliasMaps.TryGetValue(key, out var aliasMap);

        var metrics = ExtractMetricsWithAliasResolution(payload, aliasMap);

        return [new DeviceDataEvent
        {
            FormatId = FormatId,
            Topic = envelope.Topic,
            GroupId = group,
            NodeId = node,
            DeviceId = device,
            Metrics = metrics
        }];
    }

    private static bool TryParseTopic(string topic, out string group, out string verb, out string node, out string? device)
    {
        group = verb = node = string.Empty;
        device = null;

        if (!topic.StartsWith("spBv1.0/", StringComparison.Ordinal))
            return false;

        var segments = topic.Split('/');

        if (segments.Length < 4)
            return false;

        verb = segments[2];
        if (verb == "STATE")
            return false;

        group = segments[1];
        node = segments[3];
        device = segments.Length >= 5 ? segments[4] : null;
        return true;
    }

    private static IReadOnlyList<MetricSnapshot> ExtractMetrics(Payload payload)
    {
        var result = new List<MetricSnapshot>();

        foreach (var metric in payload.Metrics)
        {
            if (string.IsNullOrEmpty(metric.Name))
                continue;

            var (value, dataType) = ExtractMetricValue(metric);
            result.Add(new MetricSnapshot
            {
                Name = metric.Name,
                DataType = dataType,
                Value = value
            });
        }

        return result;
    }

    private static IReadOnlyList<MetricSnapshot> ExtractMetricsWithAliasResolution(
        Payload payload, Dictionary<ulong, string>? aliasMap)
    {
        var result = new List<MetricSnapshot>();

        foreach (var metric in payload.Metrics)
        {
            var name = !string.IsNullOrEmpty(metric.Name)
                ? metric.Name
                : aliasMap != null ? aliasMap.GetValueOrDefault(metric.Alias) : null;

            if (name == null)
                continue;

            var (value, dataType) = ExtractMetricValue(metric);
            result.Add(new MetricSnapshot
            {
                Name = name,
                DataType = dataType,
                Value = value
            });
        }

        return result;
    }

    private static (string Value, string DataType) ExtractMetricValue(Payload.Types.Metric metric)
    {
        var dataType = metric.Datatype switch
        {
            1 => "int8",
            2 => "int16",
            3 => "int32",
            4 => "int64",
            5 => "uint8",
            6 => "uint16",
            7 => "uint32",
            8 => "uint64",
            9 => "float",
            10 => "double",
            11 => "bool",
            12 => "string",
            14 => "datetime",
            15 => "text",
            16 => "uuid",
            17 => "dataset",
            18 => "bytes",
            19 => "file",
            20 => "template",
            _ => "unknown"
        };

        var value = metric.ValueCase switch
        {
            Payload.Types.Metric.ValueOneofCase.IntValue => metric.IntValue.ToString(),
            Payload.Types.Metric.ValueOneofCase.LongValue => metric.LongValue.ToString(),
            Payload.Types.Metric.ValueOneofCase.FloatValue => metric.FloatValue.ToString("F4"),
            Payload.Types.Metric.ValueOneofCase.DoubleValue => metric.DoubleValue.ToString("F4"),
            Payload.Types.Metric.ValueOneofCase.BooleanValue => metric.BooleanValue ? "true" : "false",
            Payload.Types.Metric.ValueOneofCase.StringValue => metric.StringValue,
            _ => "\u2014"
        };

        return (value, dataType);
    }
}
