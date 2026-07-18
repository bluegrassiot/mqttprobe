using MqttProbe.Services.Emulation;
using MqttProbe.Services.Plugins.Contracts;

namespace MqttProbe.Services.Plugins.BuiltIn;

public static class BuiltInPluginRegistration
{
    public static void RegisterBuiltIns(IPluginRegistrationContext context)
    {
        context.RegisterDetector(new EmptyPayloadDetector());
        context.RegisterDetector(new SparkplugPayloadDetector());
        context.RegisterDetector(new MessagePackPayloadDetector());
        context.RegisterDetector(new BinaryPayloadDetector());
        context.RegisterDetector(new JsonPayloadDetector());
        context.RegisterDetector(new XmlPayloadDetector());
        context.RegisterDetector(new HexPayloadDetector());
        context.RegisterDetector(new Base64PayloadDetector());
        context.RegisterDetector(new PlainTextPayloadDetector());

        context.RegisterDecoder(new EmptyPayloadDecoder());
        context.RegisterDecoder(new SparkplugPayloadDecoder());
        context.RegisterDecoder(new MessagePackPayloadDecoder());
        context.RegisterDecoder(new BinaryPayloadDecoder());
        context.RegisterDecoder(new JsonPayloadDecoder());
        context.RegisterDecoder(new XmlPayloadDecoder());
        context.RegisterDecoder(new HexPayloadDecoder());
        context.RegisterDecoder(new Base64PayloadDecoder());
        context.RegisterDecoder(new PlainTextPayloadDecoder());

        context.RegisterTopologyExtractor(new SparkplugTopologyExtractor());

        context.RegisterEncoder(new JsonPayloadEncoder());
        context.RegisterEncoder(new PlainTextPayloadEncoder());
        context.RegisterEncoder(new HexPayloadEncoder());
    }
}
