namespace MqttProbe.Services.Plugins.Contracts;

public interface IMqttProbePlugin
{
    public string PluginId { get; }
    public string Name { get; }
    public string? Description { get; }
    public void RegisterServices(IPluginRegistrationContext context);
}

public interface IPluginRegistrationContext
{
    public void RegisterDetector(IPayloadDetector detector);
    public void RegisterDecoder(IPayloadDecoder decoder);
    public void RegisterTopologyExtractor(ITopologyExtractor extractor);
    public void RegisterEncoder(IPayloadEncoder encoder);
    public void RegisterTemplateProvider(IPayloadTemplateProvider provider);
}
