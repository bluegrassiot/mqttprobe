using Microsoft.Extensions.Logging;
using MqttProbe.Core.Services.Plugins.BuiltIn;
using MqttProbe.Core.Services.Plugins.Pipeline;
using MqttProbe.Core.Services.Plugins.Registry;
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
