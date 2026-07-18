using MqttProbe.Models.Plugins;
using MqttProbe.Services.Plugins;

namespace MqttProbe.Shared.Tests.Services.Plugins;

[TestFixture]
public class PluginFolderDefaultsTests
{
    [Test]
    public void Apply_EmptyFolders_AddsDefaults()
    {
        var config = new PluginConfig();
        var baseDir = Path.GetTempPath();

        PluginFolderDefaults.Apply(config, baseDir, "/opt/plugins", "relative/path");

        config.PluginFolders.Should().HaveCount(2);
        config.PluginFolders[0].Should().Be(Path.GetFullPath("/opt/plugins"));
        config.PluginFolders[1].Should().Be(Path.GetFullPath("relative/path", baseDir));
    }

    [Test]
    public void Apply_EmptyFolders_SkipsNullAndWhitespaceDefaults()
    {
        var config = new PluginConfig();

        PluginFolderDefaults.Apply(config, "/base", null!, "  ", "/real");

        config.PluginFolders.Should().HaveCount(1);
        config.PluginFolders[0].Should().Be(Path.GetFullPath("/real"));
    }

    [Test]
    public void Apply_EmptyFolders_Deduplicates()
    {
        var config = new PluginConfig();

        PluginFolderDefaults.Apply(config, "/base", "/same", "/same");

        config.PluginFolders.Should().HaveCount(1);
    }

    [Test]
    public void Apply_ExistingFolders_ResolvesRelativePaths()
    {
        var config = new PluginConfig { PluginFolders = ["relative", "/absolute"] };
        var baseDir = Path.Combine(Path.GetTempPath(), "test-base");

        PluginFolderDefaults.Apply(config, baseDir);

        config.PluginFolders.Should().HaveCount(2);
        config.PluginFolders[0].Should().Be(Path.GetFullPath("relative", baseDir));
        config.PluginFolders[1].Should().Be("/absolute");
    }

    [Test]
    public void Apply_ExistingFolders_DoesNotAddDefaults()
    {
        var config = new PluginConfig { PluginFolders = ["/configured"] };

        PluginFolderDefaults.Apply(config, "/base", "/default");

        config.PluginFolders.Should().HaveCount(1);
        config.PluginFolders[0].Should().Be("/configured");
    }
}
