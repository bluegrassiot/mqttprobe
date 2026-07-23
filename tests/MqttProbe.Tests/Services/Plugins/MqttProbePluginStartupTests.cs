using Microsoft.Extensions.Logging;
using MqttProbe.Models.Plugins;
using MqttProbe.Services.Plugins;
using MqttProbe.Services.Plugins.Protobuf;

namespace MqttProbe.Tests.Services.Plugins;

[TestFixture]
public class MqttProbePluginStartupTests
{
    [Test]
    public void BuildPluginRegistry_DefaultConfig_ReturnsRegistryWithAllBuiltIns()
    {
        var config = new PluginConfig();
        var loggerFactory = Substitute.For<ILoggerFactory>();

        var registry = MqttProbePluginStartup.BuildPluginRegistry(config, loggerFactory);

        registry.Should().NotBeNull();
        registry.Detectors.Should().HaveCount(9);
        registry.Decoders.Should().HaveCount(9);
        registry.Encoders.Should().HaveCount(3);
        registry.TopologyExtractors.Should().HaveCount(1);
    }

    [Test]
    public void BuildPluginRegistry_DefaultConfig_HasExpectedDecoders()
    {
        var config = new PluginConfig();
        var loggerFactory = Substitute.For<ILoggerFactory>();

        var registry = MqttProbePluginStartup.BuildPluginRegistry(config, loggerFactory);

        registry.FindDecoder("json").Should().NotBeNull();
        registry.FindDecoder("xml").Should().NotBeNull();
        registry.FindDecoder("plaintext").Should().NotBeNull();
        registry.FindDecoder("hex").Should().NotBeNull();
        registry.FindDecoder("base64").Should().NotBeNull();
        registry.FindDecoder("binary").Should().NotBeNull();
        registry.FindDecoder("messagepack").Should().NotBeNull();
        registry.FindDecoder("sparkplug-b").Should().NotBeNull();
        registry.FindDecoder("empty").Should().NotBeNull();
    }

    [Test]
    public void BuildPluginRegistry_DefaultConfig_HasExpectedEncoders()
    {
        var config = new PluginConfig();
        var loggerFactory = Substitute.For<ILoggerFactory>();

        var registry = MqttProbePluginStartup.BuildPluginRegistry(config, loggerFactory);

        registry.FindEncoder("json").Should().NotBeNull();
        registry.FindEncoder("plaintext").Should().NotBeNull();
        registry.FindEncoder("hex").Should().NotBeNull();
    }

    [Test]
    public void BuildPluginRegistry_DefaultConfig_HasSparkplugTopologyExtractor()
    {
        var config = new PluginConfig();
        var loggerFactory = Substitute.For<ILoggerFactory>();

        var registry = MqttProbePluginStartup.BuildPluginRegistry(config, loggerFactory);

        registry.FindTopologyExtractor("sparkplug-b").Should().NotBeNull();
    }

    [Test]
    public void BuildPluginRegistry_NonexistentPluginFolder_DoesNotThrow_ReturnsBuiltIns()
    {
        var config = new PluginConfig { PluginFolders = ["/nonexistent/path/that/does/not/exist"] };
        var loggerFactory = Substitute.For<ILoggerFactory>();

        var act = () => MqttProbePluginStartup.BuildPluginRegistry(config, loggerFactory);

        act.Should().NotThrow();
        var registry = act();
        registry.Detectors.Should().HaveCount(9);
        registry.Decoders.Should().HaveCount(9);
    }

    [Test]
    public void BuildPluginRegistry_DisabledPluginIds_PassedThroughToRegistry()
    {
        var config = new PluginConfig { DisabledPluginIds = ["some-disabled-plugin"] };
        var loggerFactory = Substitute.For<ILoggerFactory>();

        var registry = MqttProbePluginStartup.BuildPluginRegistry(config, loggerFactory);

        // Built-ins still present; disabled ID recorded in diagnostics.
        registry.Detectors.Should().HaveCount(9);
        registry.Diagnostics.Should().Contain(d =>
            d.Source == "some-disabled-plugin" &&
            d.Message.Contains("disabled"));
    }

    [Test]
    public void BuildPluginRegistry_Overrides_PassedThroughToRegistry()
    {
        var config = new PluginConfig
        {
            Overrides =
            [
                new PluginOverrideConfig
                {
                    FormatId = "json",
                    Capability = "Decoder",
                    PluginId = "custom-plugin"
                }
            ]
        };
        var loggerFactory = Substitute.For<ILoggerFactory>();

        var registry = MqttProbePluginStartup.BuildPluginRegistry(config, loggerFactory);

        // Override references a plugin that didn't register; with a single provider
        // (the built-in), the override is silently ignored and the built-in wins.
        registry.FindDecoder("json").Should().NotBeNull();
    }

    [Test]
    public void BuildPluginRegistry_RootedProtobufSchemaFile_DoesNotThrow()
    {
        var pluginFolder = Path.Combine(Path.GetTempPath(), "mqttprobe-plugins-" + Guid.NewGuid().ToString("N"));
        var schemaRoot = Path.Combine(pluginFolder, ProtobufSchemaFolderLoader.FolderName);
        Directory.CreateDirectory(schemaRoot);
        var rootedPath = Path.Combine(schemaRoot, "demo.proto");
        File.WriteAllText(rootedPath, "syntax = \"proto3\"; package demo; message Reading { int32 id = 1; }");
        File.WriteAllText(Path.Combine(schemaRoot, ProtobufSchemaFolderLoader.ManifestFileName),
            $$"""
              {
                "schemas": [
                  {
                    "files": [ {{System.Text.Json.JsonSerializer.Serialize(rootedPath)}} ],
                    "topicPattern": "sensors/+/data",
                    "messageType": "demo.Reading"
                  }
                ]
              }
              """);
        var config = new PluginConfig { PluginFolders = { pluginFolder } };
        var loggerFactory = Substitute.For<ILoggerFactory>();

        var act = () => MqttProbePluginStartup.BuildPluginRegistry(config, loggerFactory);

        act.Should().NotThrow();
        act().Should().NotBeNull();
    }

    [Test]
    public void BuildPluginRegistry_MalformedProtobufManifest_DoesNotThrow()
    {
        var pluginFolder = Path.Combine(Path.GetTempPath(), "mqttprobe-plugins-" + Guid.NewGuid().ToString("N"));
        var schemaRoot = Path.Combine(pluginFolder, ProtobufSchemaFolderLoader.FolderName);
        Directory.CreateDirectory(schemaRoot);
        File.WriteAllText(Path.Combine(schemaRoot, ProtobufSchemaFolderLoader.ManifestFileName), "{ not json");
        var config = new PluginConfig { PluginFolders = { pluginFolder } };
        var loggerFactory = Substitute.For<ILoggerFactory>();

        var act = () => MqttProbePluginStartup.BuildPluginRegistry(config, loggerFactory);

        act.Should().NotThrow();
        act().FindDecoder("protobuf").Should().BeNull();
    }
}
