using Microsoft.Extensions.Logging;
using MQTTnet;
using MqttProbe.Core.Services.Plugins;
using MqttProbe.Core.Services.Plugins.BuiltIn;
using MqttProbe.Core.Services.Plugins.Pipeline;
using MqttProbe.Core.Services.Plugins.Registry;
using MqttProbe.PluginContracts;

namespace MqttProbe.Tests.Utilities;

internal static class TestPipelineHelper
{
    internal static PayloadPipeline BuildBuiltInPipeline()
    {
        var builder = new PluginRegistryBuilder();
        BuiltInPluginRegistration.RegisterBuiltIns(builder);
        var registry = builder.Build();
        return new PayloadPipeline(registry, Substitute.For<ILogger<PayloadPipeline>>());
    }

    // Registers a plugin decoder that wins the sparkplug-b override, mirroring a plugin that
    // replaces the built-in decoder while still emitting valid Sparkplug wire bytes.
    internal static PayloadPipeline BuildPipelineWithDecoderOverride(
        IPayloadDecoder decoder, string pluginId = "override-plugin")
    {
        var builder = new PluginRegistryBuilder();
        BuiltInPluginRegistration.RegisterBuiltIns(builder);
        builder.RegisterPlugin(pluginId, context => context.RegisterDecoder(decoder));
        var registry = builder.Build(overrides:
        [
            new PluginOverrideConfig
            {
                Capability = "Decoder",
                FormatId = decoder.FormatId,
                PluginId = pluginId
            }
        ]);
        return new PayloadPipeline(registry, Substitute.For<ILogger<PayloadPipeline>>());
    }

    // Wraps the built-in Sparkplug decode but replaces TypedPayload with the supplied object,
    // modelling a decoder whose typed payload is null or from an isolated plugin assembly.
    internal sealed class WrappedTypedPayloadSparkplugDecoder(object? typedPayload) : IPayloadDecoder
    {
        private readonly SparkplugPayloadDecoder _inner = new();

        public string FormatId => "sparkplug-b";

        public DecodedPayloadEnvelope Decode(MqttApplicationMessageReceivedEventArgs e)
        {
            var envelope = _inner.Decode(e);
            if (envelope.IsFailure)
                return envelope;

            return DecodedPayloadEnvelope.CreateSuccess(
                envelope.FormatId,
                envelope.Topic,
                envelope.RawPayload,
                envelope.DisplayText,
                typedPayload: typedPayload);
        }
    }
}
