using MQTTnet;
using MqttProbe.Services.Plugins.Contracts;

namespace MqttProbe.Services.Plugins.Registry;

public sealed class PluginRegistry
{
    public IReadOnlyList<IPayloadDetector> Detectors { get; }
    public IReadOnlyDictionary<string, IPayloadDecoder> Decoders { get; }
    public IReadOnlyDictionary<string, ITopologyExtractor> TopologyExtractors { get; }
    public IReadOnlyDictionary<string, IPayloadEncoder> Encoders { get; }
    public IReadOnlyDictionary<string, IPayloadTemplateProvider> TemplateProviders { get; }
    public IReadOnlyList<PluginDiagnosticEntry> Diagnostics { get; }
    public IReadOnlySet<string> LoadedPackagePaths { get; }

    internal PluginRegistry(
        IReadOnlyList<IPayloadDetector> detectors,
        IReadOnlyDictionary<string, IPayloadDecoder> decoders,
        IReadOnlyDictionary<string, ITopologyExtractor> topologyExtractors,
        IReadOnlyDictionary<string, IPayloadEncoder> encoders,
        IReadOnlyDictionary<string, IPayloadTemplateProvider> templateProviders,
        IReadOnlyList<PluginDiagnosticEntry> diagnostics,
        IReadOnlySet<string> loadedPackagePaths)
    {
        Detectors = detectors;
        Decoders = decoders;
        TopologyExtractors = topologyExtractors;
        Encoders = encoders;
        TemplateProviders = templateProviders;
        Diagnostics = diagnostics;
        LoadedPackagePaths = loadedPackagePaths;
    }

    public IPayloadDetector? FindDetector(MqttApplicationMessageReceivedEventArgs e) =>
        Detectors.FirstOrDefault(detector => detector.CanDetect(e));
    public IEnumerable<IPayloadDetector> FindMatchingDetectors(MqttApplicationMessageReceivedEventArgs e) =>
        Detectors.Where(detector => detector.CanDetect(e));
    public IPayloadDecoder? FindDecoder(string formatId) =>
        Decoders.GetValueOrDefault(formatId);
    public IPayloadEncoder? FindEncoder(string formatId) =>
        Encoders.GetValueOrDefault(formatId);
    public ITopologyExtractor? FindTopologyExtractor(string formatId) =>
        TopologyExtractors.GetValueOrDefault(formatId);
}
