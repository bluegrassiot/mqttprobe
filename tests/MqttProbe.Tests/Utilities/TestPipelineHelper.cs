using Microsoft.Extensions.Logging;
using MqttProbe.Services.Plugins.BuiltIn;
using MqttProbe.Services.Plugins.Pipeline;
using MqttProbe.Services.Plugins.Registry;
using NSubstitute;

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
}
