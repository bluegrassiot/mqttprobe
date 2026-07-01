using System.Text;
using MessagePack;
using MQTTnet;
using MQTTnet.Client;
using MqttProbe.Models.Sparkplug;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Mqtt;
using Org.Eclipse.Tahu.Protobuf;

namespace MqttProbe.Services.Sparkplug;

public interface IPayloadDecoder
{
    public DecodedPayload Decode(MqttApplicationMessageReceivedEventArgs e);
    public string GetPayloadStr(MqttApplicationMessageReceivedEventArgs e);
}

public class PayloadDecoder : IPayloadDecoder
{
    private readonly ISparkplugTopologyService _topology;
    private readonly ISettingsStore _settings;

    public PayloadDecoder(ISparkplugTopologyService topology, ISettingsStore settings)
    {
        _topology = topology;
        _settings = settings;
    }

    public DecodedPayload Decode(MqttApplicationMessageReceivedEventArgs e)
    {
        var segment = e.ApplicationMessage.PayloadSegment;
        if (segment.Count == 0)
            return new DecodedPayload(string.Empty, DetectedPayloadFormat.Empty);

        var format = PayloadFormatDetector.Detect(e);

        var payload = format switch
        {
            DetectedPayloadFormat.Sparkplug => DecodeSparkplug(segment),
            DetectedPayloadFormat.Binary => DecodeBinary(segment),
            DetectedPayloadFormat.MsgPack => DecodeMessagePack(segment),
            _ => e.ApplicationMessage.ConvertPayloadToString() ?? string.Empty
        };

        IReadOnlyDictionary<ulong, string>? aliasNames = null;
        if (format == DetectedPayloadFormat.Sparkplug
            && _settings.Config.Ui.EnrichSparkplugAliasNames)
        {
            aliasNames = ResolveAliasNames(e.ApplicationMessage.Topic, segment);
        }

        return new DecodedPayload(payload, format, aliasNames);
    }

    public string GetPayloadStr(MqttApplicationMessageReceivedEventArgs e) =>
        Decode(e).Payload;

    private IReadOnlyDictionary<ulong, string>? ResolveAliasNames(
        string topic, ArraySegment<byte> segment)
    {
        if (!SparkplugTopologyService.TryParseTopic(
                topic, out var group, out var verb, out var node, out var device))
            return null;

        Payload? parsed;
        try
        {
            parsed = Payload.Parser.ParseFrom(segment.ToArray());
        }
        catch
        {
            return null;
        }

        if (parsed is null)
            return null;

        Dictionary<ulong, string>? aliasMap = null;

        if (_topology.Groups.TryGetValue(group, out var grp)
            && grp.Nodes.TryGetValue(node, out var nodeObj))
        {
            if (device is not null
                && nodeObj.Devices.TryGetValue(device, out var devObj))
            {
                lock (devObj.SyncRoot)
                {
                    aliasMap = devObj.AliasMap.Count > 0
                        ? new Dictionary<ulong, string>(devObj.AliasMap)
                        : null;
                }
            }
            else
            {
                lock (nodeObj.SyncRoot)
                {
                    aliasMap = nodeObj.AliasMap.Count > 0
                        ? new Dictionary<ulong, string>(nodeObj.AliasMap)
                        : null;
                }
            }
        }

        if (aliasMap is null)
            return null;

        var result = new Dictionary<ulong, string>();
        foreach (var metric in parsed.Metrics)
        {
            if (metric.Alias != 0
                && string.IsNullOrEmpty(metric.Name)
                && aliasMap.TryGetValue(metric.Alias, out var resolvedName))
            {
                result[metric.Alias] = resolvedName;
            }
        }

        return result.Count > 0 ? result : null;
    }

    private static string DecodeSparkplug(ArraySegment<byte> segment)
    {
        try
        {
            return Payload.Parser.ParseFrom(segment.ToArray()).ToString();
        }
        catch
        {
            return Encoding.UTF8.GetString(segment);
        }
    }

    private static string DecodeBinary(ArraySegment<byte> segment)
    {
        var span = segment.Array.AsSpan(segment.Offset, segment.Count);
        var sb = new StringBuilder(span.Length * 2);
        foreach (var b in span)
            sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    private static string DecodeMessagePack(ArraySegment<byte> segment)
    {
        try
        {
            return MessagePackSerializer.ConvertToJson(segment.ToArray());
        }
        catch (MessagePackSerializationException)
        {
            return DecodeBinary(segment);
        }
    }
}

public sealed record DecodedPayload(
    string Payload,
    DetectedPayloadFormat Format,
    IReadOnlyDictionary<ulong, string>? AliasNames = null);
