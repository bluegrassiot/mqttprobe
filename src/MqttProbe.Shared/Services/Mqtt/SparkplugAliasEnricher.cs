using MQTTnet;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Plugins.Contracts;
using MqttProbe.Services.Plugins.Pipeline;
using MqttProbe.Services.Sparkplug;

namespace MqttProbe.Services.Mqtt;

internal sealed class SparkplugAliasEnricher(IUiSettings uiSettings, ISparkplugTopologyService? topologyService)
{
    // Resolves against live topology state, so callers must apply the decode's topology
    // events before calling this or births in the same message resolve to nothing.
    public IReadOnlyDictionary<ulong, string>? Resolve(
        string topic, MqttApplicationMessageReceivedEventArgs arg, PipelineDecodeResult result)
    {
        if (result.Envelope.FormatId != "sparkplug-b"
            || result.Envelope.IsFailure
            || !uiSettings.Ui.EnrichSparkplugAliasNames
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
