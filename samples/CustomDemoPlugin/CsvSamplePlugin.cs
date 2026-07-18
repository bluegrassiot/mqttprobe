using MqttProbe.Services.Plugins.Contracts;

namespace CustomDemoPlugin;

public sealed class CsvSamplePlugin : IMqttProbePlugin
{
    public string PluginId => "sample-csv";
    public string Name => "CSV Sample Plugin";
    public string? Description => "Sample external plugin that detects and decodes CSV sensor payloads.";

    public void RegisterServices(IPluginRegistrationContext context)
    {
        context.RegisterDetector(new CsvPayloadDetector());
        context.RegisterDecoder(new CsvPayloadDecoder());
    }
}
