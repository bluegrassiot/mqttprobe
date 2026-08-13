using MQTTnet;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Plugins.Pipeline;
using MqttProbe.Core.Services.Sparkplug;
using MqttProbe.PluginContracts;

namespace MqttProbe.Core.Services.Mqtt;

internal sealed class SparkplugAliasEnricher(ISparkplugSettings sparkplugSettings, ISparkplugTopologyService? topologyService)
{
    // Resolves against live topology state, so callers must apply the decode's topology
    // events before calling this or births in the same message resolve to nothing.
    public IReadOnlyDictionary<ulong, string>? Resolve(
        string topic, MqttApplicationMessageReceivedEventArgs arg, PipelineDecodeResult result)
    {
        if (result.Envelope.FormatId != "sparkplug-b"
            || result.Envelope.IsFailure
            || !sparkplugSettings.Sparkplug.EnrichAliasNames
            || topologyService is null)
        {
            return null;
        }

        var rawPayload = arg.ApplicationMessage.GetPayloadSegment().Count > 0
            ? arg.ApplicationMessage.GetPayloadSegment().ToArray()
            : [];
        return SparkplugAliasResolver.Resolve(topic, rawPayload, topologyService.Groups);
    }
}
