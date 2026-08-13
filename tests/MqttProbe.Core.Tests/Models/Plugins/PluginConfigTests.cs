using Microsoft.Extensions.Configuration;
using MqttProbe.Core.Models.Plugins;

namespace MqttProbe.Core.Tests.Models.Plugins;

[TestFixture]
public class PluginConfigTests
{
    [Test]
    public void DisabledPluginIds_ObjectInitializer_KeepsCaseInsensitiveComparer()
    {
        var config = new PluginConfig { DisabledPluginIds = ["Fixture-Valid"] };

        config.DisabledPluginIds.Contains("fixture-valid").Should().BeTrue();
    }

    [Test]
    public void DisabledPluginIds_ConfigurationBinder_KeepsCaseInsensitiveComparer()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Plugins:DisabledPluginIds:0"] = "Sample-CSV"
            })
            .Build();

        var config = configuration.GetSection("Plugins").Get<PluginConfig>()!;

        config.DisabledPluginIds.Contains("sample-csv").Should().BeTrue();
    }

    [Test]
    public void DisabledPluginIds_DefaultsToEmpty()
    {
        new PluginConfig().DisabledPluginIds.Should().BeEmpty();
    }
}
