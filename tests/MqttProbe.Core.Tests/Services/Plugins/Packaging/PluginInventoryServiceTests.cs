using Microsoft.Extensions.Logging.Abstractions;
using MqttProbe.Core.Models.Plugins;
using MqttProbe.Core.Services.Plugins;
using MqttProbe.Core.Services.Plugins.Packaging;
using MqttProbe.Core.Services.Plugins.Pipeline;
using MqttProbe.Core.Services.Plugins.Protobuf;
using MqttProbe.Core.Services.Plugins.Registry;

namespace MqttProbe.Core.Tests.Services.Plugins.Packaging;

[TestFixture]
public class PluginInventoryServiceTests
{
    private string _pluginFolder = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _pluginFolder = Path.Combine(Path.GetTempPath(), "mqttprobe-inv-" + Guid.NewGuid().ToString("N"));
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

    private string InstallSchemaManifest(string id, string version = "1.0.0")
    {
        var root = Path.Combine(_pluginFolder, ProtobufSchemaFolderLoader.FolderName, id);
        Directory.CreateDirectory(root);

        File.WriteAllText(Path.Combine(root, PluginManifestValidator.FileName),
            $$"""
            { "id": "{{id}}", "name": "{{id}} schemas", "version": "{{version}}", "kind": "protobuf-schemas" }
            """);

        return root;
    }

    private PluginInventoryService CreateService(
        PluginConfig config, PluginRegistry registry, PluginInstallSession session)
    {
        var pipeline = new PayloadPipeline(registry, NullLogger<PayloadPipeline>.Instance);
        return new PluginInventoryService(config, pipeline, session);
    }

    private static PluginRegistry RegistryWith(
        IEnumerable<string>? loadedPaths = null,
        IEnumerable<PluginDiagnosticEntry>? diagnostics = null)
    {
        var builder = new PluginRegistryBuilder();

        foreach (var path in loadedPaths ?? [])
        {
            builder.RegisterPackagePath(path);
        }

        foreach (var diagnostic in diagnostics ?? [])
        {
            builder.AddDiagnostic(diagnostic);
        }

        return builder.Build([], []);
    }

    private PluginConfig ConfigForFolder()
    {
        var config = new PluginConfig();
        config.PluginFolders.Add(_pluginFolder);
        return config;
    }

    [Test]
    public void Reports_A_Loaded_Package_As_Active()
    {
        var root = InstallSchemaManifest("demo");

        var inventory = CreateService(ConfigForFolder(), RegistryWith([root]), new PluginInstallSession())
            .GetInstalled();

        inventory.Should().ContainSingle();
        inventory[0].Manifest.Id.Should().Be("demo");
        inventory[0].Manifest.Version.Should().Be("1.0.0");
        inventory[0].Status.Should().Be(PluginStatus.Active);
    }

    [Test]
    public void Reports_A_Package_With_A_Matching_Error_Diagnostic_As_Failed()
    {
        var root = InstallSchemaManifest("demo");

        var registry = RegistryWith(diagnostics:
        [
            new PluginDiagnosticEntry
            {
                Source = "protobuf",
                SourcePath = root,
                Severity = DiagnosticSeverity.Error,
                Message = "Failed to load protobuf schemas."
            }
        ]);

        var inventory = CreateService(ConfigForFolder(), registry, new PluginInstallSession()).GetInstalled();

        inventory[0].Status.Should().Be(PluginStatus.Failed);
        inventory[0].StatusDetail.Should().Contain("Failed to load protobuf schemas.");
    }

    [Test]
    public void Reports_A_Loaded_Package_As_Active_When_The_Configured_Plugin_Folder_Has_Redundant_Segments()
    {
        var root = InstallSchemaManifest("demo");

        // The redundant ".." segment sits in the *configured* plugin folder, so every
        // candidate directory GetInstalled discovers is non-canonical text; only its own
        // Path.GetFullPath(candidate) call can bring it back in line with the registry's
        // (already canonical) loaded-path entry.
        var config = new PluginConfig();
        config.PluginFolders.Add(Path.Combine(_pluginFolder, ProtobufSchemaFolderLoader.FolderName, ".."));

        var inventory = CreateService(config, RegistryWith([root]), new PluginInstallSession())
            .GetInstalled();

        inventory[0].Status.Should().Be(PluginStatus.Active);
    }

    [Test]
    public void Reports_A_Package_As_Failed_When_Diagnostic_Source_Path_Has_Redundant_Segments()
    {
        InstallSchemaManifest("demo");

        // The redundant segment detours through a sibling directory ("sibling-package")
        // before backtracking with "..", so the raw, unresolved text does NOT start with
        // the install path - only Path.GetFullPath's resolution makes IsWithin match. A
        // trailing "../demo" straight off the install path would still satisfy a naive
        // string prefix check even without resolving it, masking the guard.
        var protobufFolder = Path.Combine(_pluginFolder, ProtobufSchemaFolderLoader.FolderName);
        var noisySourcePath = Path.Combine(protobufFolder, "sibling-package", "..", "demo");

        var registry = RegistryWith(diagnostics:
        [
            new PluginDiagnosticEntry
            {
                Source = "protobuf",
                SourcePath = noisySourcePath,
                Severity = DiagnosticSeverity.Error,
                Message = "Failed to load protobuf schemas."
            }
        ]);

        var inventory = CreateService(ConfigForFolder(), registry, new PluginInstallSession()).GetInstalled();

        inventory[0].Status.Should().Be(PluginStatus.Failed);
        inventory[0].StatusDetail.Should().Contain("Failed to load protobuf schemas.");
    }

    [Test]
    public void Reports_A_Newly_Installed_Schema_Bundle_As_Pending_Activation()
    {
        InstallSchemaManifest("demo");

        var session = new PluginInstallSession();
        session.Record("demo", requiresRestart: false);

        CreateService(ConfigForFolder(), RegistryWith(), session)
            .GetInstalled()[0].Status.Should().Be(PluginStatus.PendingActivation);
    }

    [Test]
    public void Reports_A_Newly_Installed_Assembly_As_Pending_Restart()
    {
        var root = Path.Combine(_pluginFolder, "demoplugin");
        Directory.CreateDirectory(root);

        File.WriteAllText(Path.Combine(root, PluginManifestValidator.FileName),
            """
            { "id": "demoplugin", "name": "Demo", "version": "1.0.0", "kind": "assembly" }
            """);

        var session = new PluginInstallSession();
        session.Record("demoplugin", requiresRestart: true);

        CreateService(ConfigForFolder(), RegistryWith(), session)
            .GetInstalled()[0].Status.Should().Be(PluginStatus.PendingRestart);
    }

    [Test]
    public void Reports_A_Disabled_Plugin_As_Disabled()
    {
        var root = InstallSchemaManifest("demo");

        var config = ConfigForFolder();
        config.DisabledPluginIds.Add("demo");

        CreateService(config, RegistryWith([root]), new PluginInstallSession())
            .GetInstalled()[0].Status.Should().Be(PluginStatus.Disabled);
    }

    [Test]
    public void Reports_An_Unloaded_Package_With_No_Diagnostic_As_Failed()
    {
        InstallSchemaManifest("demo");

        var inventory = CreateService(ConfigForFolder(), RegistryWith(), new PluginInstallSession())
            .GetInstalled();

        inventory[0].Status.Should().Be(PluginStatus.Failed);
        inventory[0].StatusDetail.Should().Contain("not loaded");
    }

    [Test]
    public void Ignores_Directories_Without_A_Package_Manifest()
    {
        Directory.CreateDirectory(Path.Combine(_pluginFolder, "not-a-package"));

        CreateService(ConfigForFolder(), RegistryWith(), new PluginInstallSession())
            .GetInstalled().Should().BeEmpty();
    }

    [Test]
    public void Ignores_Reserved_Staging_And_Pending_Directories_Directly_Under_Plugin_Folder()
    {
        // The scan only looks one level below pluginFolder, so the manifest must sit
        // directly inside .staging/.pending, not nested another level under it.
        foreach (var reserved in new[] { PluginPackagePaths.StagingFolderName, PluginPackagePaths.PendingFolderName })
        {
            var path = Path.Combine(_pluginFolder, reserved);
            Directory.CreateDirectory(path);

            File.WriteAllText(Path.Combine(path, PluginManifestValidator.FileName),
                $$"""
                { "id": "{{reserved}}-plugin", "name": "Demo", "version": "1.0.0", "kind": "assembly" }
                """);
        }

        CreateService(ConfigForFolder(), RegistryWith(), new PluginInstallSession())
            .GetInstalled().Should().BeEmpty();
    }

    [Test]
    public void Ignores_Reserved_Staging_And_Pending_Directories_Directly_Under_Protobuf_Folder()
    {
        var protobufFolder = Path.Combine(_pluginFolder, ProtobufSchemaFolderLoader.FolderName);

        foreach (var reserved in new[] { PluginPackagePaths.StagingFolderName, PluginPackagePaths.PendingFolderName })
        {
            var path = Path.Combine(protobufFolder, reserved);
            Directory.CreateDirectory(path);

            File.WriteAllText(Path.Combine(path, PluginManifestValidator.FileName),
                $$"""
                { "id": "{{reserved}}-schemas", "name": "Demo", "version": "1.0.0", "kind": "protobuf-schemas" }
                """);
        }

        CreateService(ConfigForFolder(), RegistryWith(), new PluginInstallSession())
            .GetInstalled().Should().BeEmpty();
    }

    [Test]
    public void Results_Are_Sorted_By_Name()
    {
        InstallSchemaManifest("zulu");
        InstallSchemaManifest("alpha");

        CreateService(ConfigForFolder(), RegistryWith(), new PluginInstallSession())
            .GetInstalled().Select(p => p.Manifest.Id).Should().ContainInOrder("alpha", "zulu");
    }
}
