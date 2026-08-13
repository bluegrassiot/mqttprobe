using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MqttProbe.Core.Models.Plugins;
using MqttProbe.Core.Services.Plugins;
using MqttProbe.Core.Services.Plugins.Protobuf;
using MqttProbe.PluginContracts;

namespace MqttProbe.Core.Tests.Services.Plugins;

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
    public void BuildPluginRegistry_LogsContractVersionAtStartup()
    {
        var config = new PluginConfig();
        var logger = Substitute.For<ILogger>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(logger);

        MqttProbePluginStartup.BuildPluginRegistry(config, loggerFactory);

        logger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains(PluginContract.Version.ToString())),
            null,
            Arg.Any<Func<object, Exception?, string>>());
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

    private static string WriteSchemaPackage(string pluginFolder, string id, string messageType)
    {
        var root = Path.Combine(pluginFolder, ProtobufSchemaFolderLoader.FolderName, id);
        Directory.CreateDirectory(root);

        File.WriteAllText(Path.Combine(root, "demo.proto"),
            """
            syntax = "proto3";
            package demo;
            message Reading { int32 id = 1; }
            """);

        File.WriteAllText(Path.Combine(root, ProtobufSchemaFolderLoader.ManifestFileName),
            $$"""
            {
              "schemas": [
                {
                  "files": [ "demo.proto" ],
                  "topicPattern": "{{id}}/+/reading",
                  "messageType": "{{messageType}}"
                }
              ]
            }
            """);

        return root;
    }

    [Test]
    public void One_Broken_Schema_Bundle_Does_Not_Disable_The_Others()
    {
        var pluginFolder = Path.Combine(Path.GetTempPath(), "mqttprobe-startup-" + Guid.NewGuid().ToString("N"));
        var goodRoot = WriteSchemaPackage(pluginFolder, "good", "demo.Reading");
        var badRoot = WriteSchemaPackage(pluginFolder, "bad", "demo.DoesNotExist");

        var config = new PluginConfig();
        config.PluginFolders.Add(pluginFolder);

        var registry = MqttProbePluginStartup.BuildPluginRegistry(config, NullLoggerFactory.Instance);

        registry.LoadedPackagePaths.Should().Contain(goodRoot,
            "a valid bundle must still load when a sibling bundle is broken");
        registry.LoadedPackagePaths.Should().NotContain(badRoot);

        registry.Diagnostics.Should().Contain(d =>
            d.SourcePath == badRoot && d.Severity >= DiagnosticSeverity.Warning);

        Directory.Delete(pluginFolder, recursive: true);
    }

    [Test]
    public void One_Source_Throwing_During_Compilation_Does_Not_Disable_The_Others()
    {
        // A null messageType survives JSON deserialization (System.Text.Json assigns null to a
        // non-nullable reference property rather than rejecting it) and reaches
        // ProtobufSchemaRegistry.BuildRoutingTable, where Normalize(null) throws a
        // NullReferenceException. This was verified empirically: malformed .proto syntax and a
        // manifest referencing a missing .proto file are both reported through
        // ProtobufSchemaRegistry.Diagnostics rather than by throwing, so neither reaches the
        // per-source catch block; a null messageType does.
        var pluginFolder = Path.Combine(Path.GetTempPath(), "mqttprobe-startup-" + Guid.NewGuid().ToString("N"));
        var goodRoot = WriteSchemaPackage(pluginFolder, "good", "demo.Reading");

        var badRoot = Path.Combine(pluginFolder, ProtobufSchemaFolderLoader.FolderName, "bad");
        Directory.CreateDirectory(badRoot);
        File.WriteAllText(Path.Combine(badRoot, "demo.proto"),
            """
            syntax = "proto3";
            package demo;
            message Reading { int32 id = 1; }
            """);
        File.WriteAllText(Path.Combine(badRoot, ProtobufSchemaFolderLoader.ManifestFileName),
            """
            {
              "schemas": [
                {
                  "files": [ "demo.proto" ],
                  "topicPattern": "bad/+/reading",
                  "messageType": null
                }
              ]
            }
            """);

        var config = new PluginConfig();
        config.PluginFolders.Add(pluginFolder);

        var registry = MqttProbePluginStartup.BuildPluginRegistry(config, NullLoggerFactory.Instance);

        registry.LoadedPackagePaths.Should().Contain(goodRoot,
            "a valid bundle must still load when a sibling bundle's compilation throws");
        registry.LoadedPackagePaths.Should().NotContain(badRoot);

        // "Failed to load protobuf schemas" (from the per-source catch) is distinct from
        // "No schemas compiled" (the non-throwing HasAnySchemas branch also covered above),
        // so matching on it confirms the exception path, not just a diagnostic-reported failure.
        registry.Diagnostics.Should().Contain(d =>
            d.SourcePath == badRoot &&
            d.Severity == DiagnosticSeverity.Error &&
            d.Message.Contains("Failed to load protobuf schemas"));

        Directory.Delete(pluginFolder, recursive: true);
    }

    [Test]
    public void Combined_Build_Failure_Reports_Sources_As_Failed_Not_Active()
    {
        // Nothing in ProtobufSchemaRegistry's current schema-content handling can make the
        // combine step fail while every per-source probe passed (duplicates only warn), so
        // the failure is forced directly through the internal combiner seam; the assertions
        // below are what a genuinely failing combine step must still produce correctly.
        var pluginFolder = Path.Combine(Path.GetTempPath(), "mqttprobe-startup-" + Guid.NewGuid().ToString("N"));
        var rootA = WriteSchemaPackage(pluginFolder, "pkgA", "demo.Reading");
        var rootB = WriteSchemaPackage(pluginFolder, "pkgB", "demo.Reading");

        var config = new PluginConfig();
        config.PluginFolders.Add(pluginFolder);

        var registry = MqttProbePluginStartup.BuildPluginRegistry(
            config,
            NullLoggerFactory.Instance,
            assemblyCache: null,
            combineProtobufSources: (_, _) => throw new InvalidOperationException("simulated combine failure"));

        registry.FindDecoder("protobuf").Should().BeNull();
        registry.LoadedPackagePaths.Should().NotContain(rootA,
            "the combine step never succeeded, so no package backing it should read as loaded");
        registry.LoadedPackagePaths.Should().NotContain(rootB);

        registry.Diagnostics.Should().Contain(d =>
            d.SourcePath == rootA && d.Severity == DiagnosticSeverity.Error,
            "each source needs its own SourcePath so PluginInventoryService can derive Failed for it");
        registry.Diagnostics.Should().Contain(d =>
            d.SourcePath == rootB && d.Severity == DiagnosticSeverity.Error);

        Directory.Delete(pluginFolder, recursive: true);
    }

    [Test]
    public void Combined_Build_With_No_Usable_Schemas_Reports_Sources_As_Failed_Not_Active()
    {
        var pluginFolder = Path.Combine(Path.GetTempPath(), "mqttprobe-startup-" + Guid.NewGuid().ToString("N"));
        var rootA = WriteSchemaPackage(pluginFolder, "pkgA", "demo.Reading");

        var config = new PluginConfig();
        config.PluginFolders.Add(pluginFolder);

        var registry = MqttProbePluginStartup.BuildPluginRegistry(
            config,
            NullLoggerFactory.Instance,
            assemblyCache: null,
            combineProtobufSources: (sources, logger) => new ProtobufSchemaRegistry(
                new ProtobufSchemaManifest(), sources[0].SchemaRoot, logger));

        registry.FindDecoder("protobuf").Should().BeNull();
        registry.LoadedPackagePaths.Should().NotContain(rootA);
        registry.Diagnostics.Should().Contain(d =>
            d.SourcePath == rootA && d.Severity == DiagnosticSeverity.Error);

        Directory.Delete(pluginFolder, recursive: true);
    }

    [Test]
    public void Loaded_Package_Paths_Are_Empty_When_No_Packages_Are_Installed()
    {
        var pluginFolder = Path.Combine(Path.GetTempPath(), "mqttprobe-startup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(pluginFolder);

        var config = new PluginConfig();
        config.PluginFolders.Add(pluginFolder);

        MqttProbePluginStartup.BuildPluginRegistry(config, NullLoggerFactory.Instance)
            .LoadedPackagePaths.Should().BeEmpty();

        Directory.Delete(pluginFolder, recursive: true);
    }
}
