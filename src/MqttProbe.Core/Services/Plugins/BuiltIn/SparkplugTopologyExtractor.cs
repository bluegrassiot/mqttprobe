using System.Globalization;
using MqttProbe.Core.Services.Sparkplug;
using MqttProbe.PluginContracts;
using Org.Eclipse.Tahu.Protobuf;

namespace MqttProbe.Core.Services.Plugins.BuiltIn;

public sealed class SparkplugTopologyExtractor : ITopologyExtractor
{
    public string FormatId => "sparkplug-b";

    public IReadOnlyList<TopologyEvent> Extract(DecodedPayloadEnvelope envelope) =>
        Extract(envelope, null);

    public IReadOnlyList<TopologyEvent> Extract(
        DecodedPayloadEnvelope envelope, SparkplugMetricMetadataStore? metadataStore)
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
                SequenceNumber = ReadSequenceNumber(envelope.RawPayload),
                GroupId = group,
                NodeId = node,
                BirthDeathSequenceNumber = GetBirthDeathSequence(payload)
            }],
            "NCMD" when device is null => HandleNcmd(envelope, payload, group, node),
            "NDATA" => HandleNData(envelope, payload, metadataStore, group, node),
            "DBIRTH" when device != null => HandleDBirth(envelope, payload, group, node, device),
            "DDEATH" when device != null => [new DeviceDeathEvent
            {
                FormatId = FormatId,
                Topic = envelope.Topic,
                SequenceNumber = ReadSequenceNumber(envelope.RawPayload),
                GroupId = group,
                NodeId = node,
                DeviceId = device
            }],
            "DDATA" when device != null => HandleDData(envelope, payload, metadataStore, group, node, device),
            _ => []
        };
    }

    private IReadOnlyList<TopologyEvent> HandleNBirth(
        DecodedPayloadEnvelope envelope, Payload payload, string group, string node)
    {
        var metrics = ExtractMetrics(payload);

        return [new NodeBirthEvent
        {
            FormatId = FormatId,
            Topic = envelope.Topic,
            TimestampUtc = GetSourceTimestamp(payload, DateTime.UtcNow),
            SequenceNumber = ReadSequenceNumber(envelope.RawPayload),
            GroupId = group,
            NodeId = node,
            Metrics = metrics,
            BirthDeathSequenceNumber = GetBirthDeathSequence(payload)
        }];
    }

    private IReadOnlyList<TopologyEvent> HandleNcmd(
        DecodedPayloadEnvelope envelope, Payload payload, string group, string node)
    {
        var isRebirthRequest = payload.Metrics.Any(metric =>
            string.Equals(metric.Name, SparkplugControlMetrics.NodeRebirth, StringComparison.Ordinal)
            && metric.ValueCase == Payload.Types.Metric.ValueOneofCase.BooleanValue
            && metric.BooleanValue);

        return isRebirthRequest
            ? [new NodeRebirthObservedEvent
            {
                FormatId = FormatId,
                Topic = envelope.Topic,
                GroupId = group,
                NodeId = node
            }]
            : [];
    }

    private IReadOnlyList<TopologyEvent> HandleNData(
        DecodedPayloadEnvelope envelope, Payload payload, SparkplugMetricMetadataStore? metadataStore,
        string group, string node)
    {
        var metrics = ExtractMetricsWithAliasResolution(payload, metadataStore, group, node, null);

        return [new NodeDataEvent
        {
            FormatId = FormatId,
            Topic = envelope.Topic,
            SequenceNumber = ReadSequenceNumber(envelope.RawPayload),
            GroupId = group,
            NodeId = node,
            Metrics = metrics
        }];
    }

    private IReadOnlyList<TopologyEvent> HandleDBirth(
        DecodedPayloadEnvelope envelope, Payload payload, string group, string node, string device)
    {
        var metrics = ExtractMetrics(payload);

        return [new DeviceBirthEvent
        {
            FormatId = FormatId,
            Topic = envelope.Topic,
            SequenceNumber = ReadSequenceNumber(envelope.RawPayload),
            GroupId = group,
            NodeId = node,
            DeviceId = device,
            Metrics = metrics
        }];
    }

    private IReadOnlyList<TopologyEvent> HandleDData(
        DecodedPayloadEnvelope envelope, Payload payload, SparkplugMetricMetadataStore? metadataStore,
        string group, string node, string device)
    {
        var metrics = ExtractMetricsWithAliasResolution(payload, metadataStore, group, node, device);

        return [new DeviceDataEvent
        {
            FormatId = FormatId,
            Topic = envelope.Topic,
            SequenceNumber = ReadSequenceNumber(envelope.RawPayload),
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

    private static List<MetricSnapshot> ExtractMetrics(Payload payload)
    {
        var result = new List<MetricSnapshot>();

        foreach (var metric in payload.Metrics)
        {
            if (string.IsNullOrEmpty(metric.Name))
                continue;

            var (value, dataType) = ExtractMetricValue(metric, metric.Datatype);
            if (dataType.Length == 0)
                dataType = "unknown";
            result.Add(new MetricSnapshot
            {
                Name = metric.Name,
                DataType = dataType,
                Value = value,
                Alias = metric.Alias != 0 ? metric.Alias : null,
                EditValue = ExtractEditValue(metric)
            });
        }

        return result;
    }

    private static ulong? GetBirthDeathSequence(Payload payload)
    {
        var metric = payload.Metrics.FirstOrDefault(item => string.Equals(item.Name, "bdSeq", StringComparison.Ordinal));
        if (metric is null)
            return null;
        return metric.ValueCase switch
        {
            Payload.Types.Metric.ValueOneofCase.IntValue when metric.IntValue >= 0 => (ulong)metric.IntValue,
            Payload.Types.Metric.ValueOneofCase.LongValue when metric.LongValue >= 0 => metric.LongValue,
            _ => null
        };
    }

    private static DateTime GetSourceTimestamp(Payload payload, DateTime fallback) =>
        payload.Timestamp == 0
            ? fallback
            : DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Min(payload.Timestamp, (ulong)DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())).UtcDateTime;

    private static ulong? ReadSequenceNumber(ReadOnlySpan<byte> payload)
    {
        var position = 0;
        ulong? sequenceNumber = null;
        while (position < payload.Length)
        {
            if (!TryReadVarint(payload, ref position, out var tag))
                return null;
            if ((tag >> 3) > 0x1FFFFFFF)
                return null;
            var field = (int)(tag >> 3);
            var wireType = (int)(tag & 7);
            if (field == 0)
                return null;

            if (field == 3 && wireType == 0)
            {
                if (!TryReadVarint(payload, ref position, out var sequence))
                    return null;
                sequenceNumber = sequence;
                continue;
            }

            switch (wireType)
            {
                case 0:
                    if (!TryReadVarint(payload, ref position, out _)) return null;
                    break;
                case 1:
                    if (payload.Length - position < 8) return null;
                    position += 8;
                    break;
                case 2:
                    if (!TryReadVarint(payload, ref position, out var length)
                        || length > int.MaxValue
                        || (int)length > payload.Length - position) return null;
                    position += (int)length;
                    break;
                case 5:
                    if (payload.Length - position < 4) return null;
                    position += 4;
                    break;
                default:
                    return null;
            }

            if (position > payload.Length)
                return null;
        }

        return sequenceNumber ?? 0UL;
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> payload, ref int position, out ulong value)
    {
        value = 0;
        for (var shift = 0; shift < 70 && position < payload.Length; shift += 7)
        {
            var current = payload[position++];
            if (shift == 63 && current > 1)
                return false;
            value |= (ulong)(current & 0x7f) << shift;
            if ((current & 0x80) == 0)
                return true;
        }

        return false;
    }

    private static List<MetricSnapshot> ExtractMetricsWithAliasResolution(
        Payload payload, SparkplugMetricMetadataStore? metadataStore,
        string group, string node, string? device)
    {
        var result = new List<MetricSnapshot>();

        foreach (var metric in payload.Metrics)
        {
            string? name;
            if (!string.IsNullOrEmpty(metric.Name))
                name = metric.Name;
            else if (metadataStore != null && metric.Alias != 0)
                name = metadataStore.ResolveName(group, node, device, metric.Alias);
            else
                name = null;

            if (name == null)
                continue;

            var datatype = ResolveDatatype(metric, name, metadataStore, group, node, device);
            var (value, dataType) = ExtractMetricValue(metric, datatype);
            result.Add(new MetricSnapshot
            {
                Name = name,
                DataType = dataType,
                Value = value,
                Alias = metric.Alias != 0 ? metric.Alias : null
            });
        }

        return result;
    }

    private static uint ResolveDatatype(
        Payload.Types.Metric metric, string name, SparkplugMetricMetadataStore? metadataStore,
        string group, string node, string? device)
    {
        if (metric.Datatype != 0 || metadataStore is null)
            return metric.Datatype;

        return metadataStore.ResolveDatatype(group, node, device, metric.Alias, name) ?? 0;
    }

    private static (string Value, string DataType) ExtractMetricValue(
        Payload.Types.Metric metric, uint datatype)
    {
        var dataType = datatype == 0
            ? string.Empty
            : SparkplugMetricValueConverter.LabelForDatatype(datatype);

        var value = SparkplugMetricValueConverter.IsIntegerDatatype(datatype)
            ? FormatIntegerValue(metric, datatype)
            : null;

        value ??= metric.ValueCase switch
        {
            // Invariant so the displayed value matches what is on the wire rather than
            // the viewer's locale — this is a protocol diagnostic, not localised UI.
            Payload.Types.Metric.ValueOneofCase.IntValue => metric.IntValue.ToString(CultureInfo.InvariantCulture),
            Payload.Types.Metric.ValueOneofCase.LongValue => metric.LongValue.ToString(CultureInfo.InvariantCulture),
            Payload.Types.Metric.ValueOneofCase.FloatValue => metric.FloatValue.ToString("F4", CultureInfo.InvariantCulture),
            Payload.Types.Metric.ValueOneofCase.DoubleValue => metric.DoubleValue.ToString("F4", CultureInfo.InvariantCulture),
            Payload.Types.Metric.ValueOneofCase.BooleanValue => metric.BooleanValue ? "true" : "false",
            Payload.Types.Metric.ValueOneofCase.StringValue => metric.StringValue,
            _ => "\u2014"
        };

        return (value, dataType);
    }

    private static string? ExtractEditValue(Payload.Types.Metric metric) => metric.ValueCase switch
    {
        Payload.Types.Metric.ValueOneofCase.FloatValue => metric.FloatValue.ToString("R", CultureInfo.InvariantCulture),
        Payload.Types.Metric.ValueOneofCase.DoubleValue => metric.DoubleValue.ToString("R", CultureInfo.InvariantCulture),
        _ => null
    };

    private static string? FormatIntegerValue(Payload.Types.Metric metric, uint datatype)
    {
        ulong rawValue;
        switch (metric.ValueCase)
        {
            case Payload.Types.Metric.ValueOneofCase.IntValue:
                rawValue = metric.IntValue;
                break;
            case Payload.Types.Metric.ValueOneofCase.LongValue:
                rawValue = metric.LongValue;
                break;
            default:
                return null;
        }

        return SparkplugMetricValueConverter.DecodeInteger(datatype, rawValue)
            .ToString(CultureInfo.InvariantCulture);
    }
}
