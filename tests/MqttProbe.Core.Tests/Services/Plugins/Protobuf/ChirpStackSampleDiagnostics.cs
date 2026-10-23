using MqttProbe.Core.Services.Plugins.Protobuf;

namespace MqttProbe.Core.Tests.Services.Plugins.Protobuf;

internal static class ChirpStackSampleDiagnostics
{
    private static readonly string[] _knownWarnings =
    [
        "WARN 20: gw/gw.proto#16: import not used: 'google/protobuf/struct.proto'",
        "WARN: topic pattern '+/gateway/+/event/up' overlaps the Sparkplug B namespace (spBv1.0). Sparkplug decoding takes precedence there; the protobuf decoder will not claim those topics.",
        "WARN: topic pattern '+/gateway/+/event/stats' overlaps the Sparkplug B namespace (spBv1.0). Sparkplug decoding takes precedence there; the protobuf decoder will not claim those topics.",
        "WARN: topic pattern '+/gateway/+/event/ack' overlaps the Sparkplug B namespace (spBv1.0). Sparkplug decoding takes precedence there; the protobuf decoder will not claim those topics.",
        "WARN: topic pattern '+/gateway/+/command/down' overlaps the Sparkplug B namespace (spBv1.0). Sparkplug decoding takes precedence there; the protobuf decoder will not claim those topics.",
        "WARN: topic pattern '+/gateway/+/state/conn' overlaps the Sparkplug B namespace (spBv1.0). Sparkplug decoding takes precedence there; the protobuf decoder will not claim those topics.",
    ];

    public static void ShouldMatch(ProtobufSchemaRegistry registry) =>
        registry.Diagnostics.Should().BeEquivalentTo(_knownWarnings);
}
