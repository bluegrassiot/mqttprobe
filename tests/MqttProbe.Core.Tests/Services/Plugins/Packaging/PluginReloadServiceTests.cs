using Microsoft.Extensions.Logging.Abstractions;
using MqttProbe.Core.Models.Plugins;
using MqttProbe.Core.Services.Plugins;
using MqttProbe.Core.Services.Plugins.Loading;
using MqttProbe.Core.Services.Plugins.Packaging;
using MqttProbe.Core.Services.Plugins.Pipeline;
using MqttProbe.Core.Services.Plugins.Protobuf;

namespace MqttProbe.Core.Tests.Services.Plugins.Packaging;

[TestFixture]
public class PluginReloadServiceTests
{
    private string _pluginFolder = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _pluginFolder = Path.Combine(Path.GetTempPath(), "mqttprobe-reload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_pluginFolder);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_pluginFolder))
        {
            Directory.Delete(_pluginFolder, recursive: true);
        }
    }

    private string InstallBundle(string id)
    {
        var root = Path.Combine(_pluginFolder, ProtobufSchemaFolderLoader.FolderName, id);
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
                { "files": [ "demo.proto" ], "topicPattern": "{{id}}/+/reading", "messageType": "demo.Reading" }
              ]
            }
            """);

        return root;
    }

    private (PluginReloadService Service, PayloadPipeline Pipeline, PluginInstallSession Session) Create()
    {
        var config = new PluginConfig();
        config.PluginFolders.Add(_pluginFolder);

        var cache = new PluginAssemblyCache();
        var registry = MqttProbePluginStartup.BuildPluginRegistry(config, NullLoggerFactory.Instance, cache);
        var pipeline = new PayloadPipeline(registry, NullLogger<PayloadPipeline>.Instance);
        var session = new PluginInstallSession();

        var service = new PluginReloadService(
            config, pipeline, session, cache, NullLoggerFactory.Instance);

        return (service, pipeline, session);
    }

    [Test]
    public void Reload_Picks_Up_A_Bundle_Installed_After_Startup()
    {
        var (service, pipeline, _) = Create();

        pipeline.Registry.LoadedPackagePaths.Should().BeEmpty();

        var root = InstallBundle("demo");

        service.Reload().Succeeded.Should().BeTrue();
        pipeline.Registry.LoadedPackagePaths.Should().Contain(Path.GetFullPath(root));
    }

    [Test]
    public void Reload_Clears_The_Pending_Install_Session()
    {
        var (service, _, session) = Create();
        InstallBundle("demo");
        session.Record("demo", requiresRestart: false);

        service.Reload();

        session.HasPending.Should().BeFalse();
    }

    [Test]
    public void Reload_Clears_Only_The_NonRestart_Entries_From_The_Pending_Session()
    {
        var (service, _, session) = Create();
        InstallBundle("demo");
        session.Record("demo", requiresRestart: false);
        session.Record("demoplugin", requiresRestart: true);

        service.Reload();

        session.TryGet("demo", out _).Should().BeFalse(
            "the schema change was actually applied by this reload");
        session.TryGet("demoplugin", out var requiresRestart).Should().BeTrue(
            "clearing the whole session would drop the assembly entry before it has a restart to apply on");
        requiresRestart.Should().BeTrue();
    }

    [Test]
    public void Reload_Replaces_The_Registry_Instance()
    {
        var (service, pipeline, _) = Create();
        var before = pipeline.Registry;

        service.Reload();

        pipeline.Registry.Should().NotBeSameAs(before);
    }

    [Test]
    public void Decoding_Concurrently_With_A_Reload_Always_Sees_A_Usable_Registry()
    {
        var (service, pipeline, _) = Create();
        InstallBundle("demo");

        var failures = 0;

        var reader = Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
            {
                if (pipeline.Registry is null)
                {
                    Interlocked.Increment(ref failures);
                }
            }
        });

        var writer = Task.Run(() =>
        {
            for (var i = 0; i < 20; i++)
            {
                service.Reload();
            }
        });

        Task.WaitAll(reader, writer);

        failures.Should().Be(0);
    }

    [Test]
    public void Reload_Leaves_The_Previous_Registry_And_Pending_Session_When_The_Build_Throws()
    {
        var config = new PluginConfig();
        config.PluginFolders.Add(_pluginFolder);

        var cache = new PluginAssemblyCache();
        var registry = MqttProbePluginStartup.BuildPluginRegistry(config, NullLoggerFactory.Instance, cache);
        var pipeline = new PayloadPipeline(registry, NullLogger<PayloadPipeline>.Instance);
        var session = new PluginInstallSession();
        session.Record("demo", requiresRestart: false);

        var service = new PluginReloadService(config, pipeline, session, cache, NullLoggerFactory.Instance);

        // A null entry is the only empirically-confirmed unguarded throw site in BuildPluginRegistry:
        // PluginRegistryBuilder.Build's BuildOverrideMap dereferences each override directly, and that
        // call sits outside every try/catch in BuildPluginRegistry, so it reaches Reload()'s catch
        // branch verbatim. This is OS-agnostic (a plain NullReferenceException), unlike path-based
        // attempts to make Directory enumeration throw, which turned out not to on Windows.
        config.Overrides.Add(null!);

        var outcome = service.Reload();

        outcome.Succeeded.Should().BeFalse();
        outcome.Error.Should().NotBeNull();
        pipeline.Registry.Should().BeSameAs(registry);
        session.HasPending.Should().BeTrue();
    }

    [Test]
    public void Assemblies_Are_Loaded_Once_Regardless_Of_Reload_Count()
    {
        var config = new PluginConfig();
        config.PluginFolders.Add(_pluginFolder);

        var cache = new PluginAssemblyCache();

        var first = cache.GetOrLoad(config, NullLoggerFactory.Instance);
        var second = cache.GetOrLoad(config, NullLoggerFactory.Instance);

        second.Should().BeSameAs(first,
            "reloading must not create a new AssemblyLoadContext per plugin DLL");
    }
}
