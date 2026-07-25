using MqttProbe.Services.Plugins.Packaging;
using MqttProbe.Services.Plugins.Protobuf;

namespace MqttProbe.Tests.Services.Plugins.Packaging;

[TestFixture]
public class PluginPendingOperationsTests
{
    private string _pluginFolder = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _pluginFolder = Path.Combine(Path.GetTempPath(), "mqttprobe-pending-" + Guid.NewGuid().ToString("N"));
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

    private string CreateInstalledAssembly(string id)
    {
        var path = Path.Combine(_pluginFolder, id);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, id + ".dll"), "MZ");
        return path;
    }

    [Test]
    public void Apply_Deletes_A_Directory_Marked_For_Removal()
    {
        var installPath = CreateInstalledAssembly("demoplugin");
        PluginPendingOperations.MarkForRemoval(_pluginFolder, "demoplugin");

        Directory.Exists(installPath).Should().BeTrue("removal is deferred, not immediate");

        var applied = PluginPendingOperations.Apply([_pluginFolder], null);

        applied.Should().Contain("demoplugin");
        Directory.Exists(installPath).Should().BeFalse();
    }

    [Test]
    public void Apply_Moves_A_Staged_Upgrade_Into_Place()
    {
        var installPath = CreateInstalledAssembly("demoplugin");
        File.WriteAllText(Path.Combine(installPath, "marker.txt"), "old");

        var pendingPath = Path.Combine(_pluginFolder, PluginPackagePaths.PendingFolderName, "demoplugin");
        Directory.CreateDirectory(pendingPath);
        File.WriteAllText(Path.Combine(pendingPath, "marker.txt"), "new");

        PluginPendingOperations.Apply([_pluginFolder], null);

        File.ReadAllText(Path.Combine(installPath, "marker.txt")).Should().Be("new");
        Directory.Exists(pendingPath).Should().BeFalse();
    }

    [Test]
    public void Apply_Ignores_A_Reserved_Directory_Under_Pending()
    {
        var installPath = CreateInstalledAssembly("demoplugin");
        File.WriteAllText(Path.Combine(installPath, "marker.txt"), "installed");

        var pendingRoot = Path.Combine(_pluginFolder, PluginPackagePaths.PendingFolderName);
        Directory.CreateDirectory(pendingRoot);
        var leaked = Path.Combine(pendingRoot, ".demoplugin.previous-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(leaked);
        File.WriteAllText(Path.Combine(leaked, "marker.txt"), "leaked backup");

        var applied = PluginPendingOperations.Apply([_pluginFolder], null);

        applied.Should().NotContain(Path.GetFileName(leaked));
        Directory.Exists(leaked).Should().BeTrue("a reserved dot-prefixed directory is not a staged upgrade and must be left alone");
        File.ReadAllText(Path.Combine(installPath, "marker.txt")).Should().Be("installed");
    }

    [Test]
    public void Apply_Is_A_No_Op_When_There_Is_Nothing_Pending()
    {
        PluginPendingOperations.Apply([_pluginFolder], null).Should().BeEmpty();
    }

    [Test]
    public void Apply_Ignores_A_Plugin_Folder_That_Does_Not_Exist()
    {
        var missing = Path.Combine(_pluginFolder, "no-such-folder");

        var act = () => PluginPendingOperations.Apply([missing], null);

        act.Should().NotThrow();
    }

    [Test]
    public void HasPendingOperation_Reports_A_Removal_Marker()
    {
        CreateInstalledAssembly("demoplugin");
        PluginPendingOperations.MarkForRemoval(_pluginFolder, "demoplugin");

        PluginPendingOperations.HasPendingOperation(_pluginFolder, "demoplugin").Should().BeTrue();
        PluginPendingOperations.HasPendingOperation(_pluginFolder, "other").Should().BeFalse();
    }

    [Test]
    public void Removing_A_Schema_Bundle_Deletes_It_Immediately()
    {
        var schemaPath = Path.Combine(_pluginFolder, ProtobufSchemaFolderLoader.FolderName, "demo");
        Directory.CreateDirectory(schemaPath);
        File.WriteAllText(Path.Combine(schemaPath, "demo.proto"), "syntax = \"proto3\";");

        PluginPendingOperations.RemoveNow(schemaPath).Should().BeTrue();
        Directory.Exists(schemaPath).Should().BeFalse();
    }

    [Test]
    public void PrepareStagedUpgradePath_Is_A_No_Op_When_Nothing_Is_Staged()
    {
        var act = () => PluginPendingOperations.PrepareStagedUpgradePath(_pluginFolder, "demoplugin");

        act.Should().NotThrow();
    }

    [Test]
    public void PrepareStagedUpgradePath_Called_Twice_Clears_The_First_Staged_Content_Before_Swap_Would_Run()
    {
        var first = PluginPendingOperations.PrepareStagedUpgradePath(_pluginFolder, "demoplugin");
        Directory.CreateDirectory(first);
        File.WriteAllText(Path.Combine(first, "marker.txt"), "first");

        var second = PluginPendingOperations.PrepareStagedUpgradePath(_pluginFolder, "demoplugin");

        second.Should().Be(first);
        Directory.Exists(first).Should()
            .BeFalse("a second deferred upgrade must clear the first staged directory itself, not rely on Swap's own backup-and-delete to do it later");
    }

    [Test]
    public void Apply_Ignores_A_Staged_Upgrade_Rename_Aside_Directory_And_Does_Not_Report_It_Applied()
    {
        var installPath = CreateInstalledAssembly("demoplugin");
        File.WriteAllText(Path.Combine(installPath, "marker.txt"), "installed");

        var pendingRoot = Path.Combine(_pluginFolder, PluginPackagePaths.PendingFolderName);
        Directory.CreateDirectory(pendingRoot);

        // Matches exactly what ClearStagedUpgrade names a staged upgrade it renames
        // aside before deleting, so this proves Apply's reserved-name skip already
        // covers that case without needing to force the delete itself to fail.
        var stagedPath = Path.Combine(pendingRoot, "demoplugin");
        var asidePath = Path.Combine(pendingRoot, PluginPackagePaths.BackupDirectoryName(stagedPath));
        Directory.CreateDirectory(asidePath);
        File.WriteAllText(Path.Combine(asidePath, "marker.txt"), "stale");

        var applied = PluginPendingOperations.Apply([_pluginFolder], null);

        applied.Should().NotContain(Path.GetFileName(asidePath));
        Directory.Exists(asidePath).Should().BeTrue("a reserved dot-prefixed rename-aside directory is not a staged upgrade and must be left alone");
        File.ReadAllText(Path.Combine(installPath, "marker.txt")).Should().Be("installed");
    }
}
