using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using MqttProbe.Core.Models.Plugins;
using MqttProbe.Core.Services.Platform;
using MqttProbe.Core.Services.Plugins.Packaging;
using MqttProbe.Core.Services.Plugins.Protobuf;
using static MqttProbe.Core.Tests.Services.Plugins.Packaging.PluginPackageTestData;

namespace MqttProbe.Core.Tests.Services.Plugins.Packaging;

[TestFixture]
public class PluginPackagePublisherTests
{
    private string _pluginFolder = string.Empty;

    private static string TestOutputDir => NUnit.Framework.TestContext.CurrentContext.TestDirectory;

    private static string FixtureAssemblyPath =>
        Path.Combine(Path.GetFullPath(Path.Combine(TestOutputDir, "PluginFixtures")),
            "MqttProbe.PluginLoader.Fixtures.dll");

    [SetUp]
    public void SetUp()
    {
        _pluginFolder = Path.Combine(Path.GetTempPath(), "mqttprobe-publisher-" + Guid.NewGuid().ToString("N"));
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

    private PluginPackagePublisher CreatePublisher(
        bool allowBinary = false,
        PluginInstallSession? session = null,
        PluginArchiveLimits? limits = null)
    {
        var appInfo = Substitute.For<IAppInfoService>();
        appInfo.GetVersion().Returns("1.0.3");

        var config = new PluginConfig { AllowBinaryPackages = allowBinary };
        var reader = new PluginPackageArchiveReader(
            config, appInfo, limits ?? new PluginArchiveLimits(), NullLoggerFactory.Instance);

        return new PluginPackagePublisher(
            reader, session ?? new PluginInstallSession(), NullLogger<PluginPackagePublisher>.Instance);
    }

    [Test]
    public async Task Installs_A_Validated_Schema_Package_And_Records_A_NonRestart_Change()
    {
        var session = new PluginInstallSession();
        var publisher = CreatePublisher(session: session);

        var outcome = await publisher.InstallAsync(SchemaPackage(), _pluginFolder, CancellationToken.None);

        outcome.Succeeded.Should().BeTrue(outcome.Error);
        outcome.Manifest!.Id.Should().Be("demo");
        outcome.InstallPath.Should().Be(
            Path.Combine(_pluginFolder, ProtobufSchemaFolderLoader.FolderName, "demo"));
        outcome.RequiresRestart.Should().BeFalse();
        session.TryGet("demo", out var requiresRestart).Should().BeTrue();
        requiresRestart.Should().BeFalse();
    }

    [Test]
    public async Task Replacing_A_Schema_Package_Removes_Stale_Files()
    {
        var publisher = CreatePublisher();
        var installPath = Path.Combine(_pluginFolder, ProtobufSchemaFolderLoader.FolderName, "demo");

        (await publisher.InstallAsync(SchemaPackage(version: "1.0.0"), _pluginFolder, CancellationToken.None))
            .Succeeded.Should().BeTrue();
        File.WriteAllText(Path.Combine(installPath, "stale.txt"), "stale");

        var outcome = await publisher.InstallAsync(
            SchemaPackage(version: "1.1.0"), _pluginFolder, CancellationToken.None);

        outcome.Succeeded.Should().BeTrue(outcome.Error);
        outcome.Manifest!.Version.Should().Be("1.1.0");
        File.Exists(Path.Combine(installPath, "stale.txt")).Should().BeFalse();
    }

    [Test]
    public async Task Does_Not_Change_A_Live_Install_Before_Validation_Completes()
    {
        var publisher = CreatePublisher();
        var installPath = Path.Combine(_pluginFolder, ProtobufSchemaFolderLoader.FolderName, "demo");

        (await publisher.InstallAsync(SchemaPackage(version: "1.0.0"), _pluginFolder, CancellationToken.None))
            .Succeeded.Should().BeTrue();

        var outcome = await publisher.InstallAsync(
            SchemaPackage(version: "2.0.0", messageType: "demo.NotThere"),
            _pluginFolder,
            CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        File.ReadAllText(Path.Combine(installPath, PluginManifestValidator.FileName)).Should().Contain("1.0.0");
    }

    [Test]
    public void Failed_Forward_Move_Restores_The_Previous_Install()
    {
        var publisher = CreatePublisher();
        var installPath = Path.Combine(_pluginFolder, ProtobufSchemaFolderLoader.FolderName, "demo");
        var stagedPath = Path.Combine(_pluginFolder, PluginPackagePaths.StagingFolderName, "missing");
        PluginPackageTestData.WriteSchemaFilesToDisk(installPath, version: "1.0.0");

        var act = () => publisher.Swap(stagedPath, installPath);

        act.Should().Throw<IOException>();
        File.ReadAllText(Path.Combine(installPath, PluginManifestValidator.FileName)).Should().Contain("1.0.0");
    }

    [Test]
    public void Swap_Uses_A_Hidden_Backup_Name_That_Loaders_Ignore()
    {
        var publisher = CreatePublisher();
        var installPath = Path.Combine(_pluginFolder, ProtobufSchemaFolderLoader.FolderName, "demo");
        var stagedPath = Path.Combine(_pluginFolder, PluginPackagePaths.StagingFolderName, "staged");
        PluginPackageTestData.WriteSchemaFilesToDisk(installPath, version: "1.0.0");
        PluginPackageTestData.WriteSchemaFilesToDisk(stagedPath, version: "1.1.0");

        var renamedNames = new ConcurrentQueue<string>();
        using var backupObserved = new ManualResetEventSlim();
        using var watcher = new FileSystemWatcher(Path.GetDirectoryName(installPath)!);
        watcher.NotifyFilter = NotifyFilters.DirectoryName;
        watcher.IncludeSubdirectories = false;
        watcher.EnableRaisingEvents = true;
        watcher.Renamed += (_, args) =>
        {
            if (args.Name is not { } name)
            {
                return;
            }

            renamedNames.Enqueue(name);

            if (name.StartsWith(".demo.previous-", StringComparison.Ordinal))
            {
                backupObserved.Set();
            }
        };

        publisher.Swap(stagedPath, installPath);

        backupObserved.Wait(TimeSpan.FromSeconds(1)).Should().BeTrue(
            "Swap must move the live install through a hidden backup");

        var backupName = renamedNames.Single(name => name.StartsWith(".demo.previous-", StringComparison.Ordinal));
        var leakedBackupPath = Path.Combine(Path.GetDirectoryName(installPath)!, backupName);
        PluginPackageTestData.WriteSchemaFilesToDisk(leakedBackupPath, version: "0.9.0");

        backupName.Should().StartWith(".demo.previous-");
        PluginPackagePaths.IsReservedDirectoryName(backupName).Should().BeTrue();
        ProtobufSchemaFolderLoader.Discover([_pluginFolder]).Should().ContainSingle()
            .Which.SchemaRoot.Should().Be(installPath);
        File.ReadAllText(Path.Combine(installPath, PluginManifestValidator.FileName)).Should().Contain("1.1.0");
    }

    [Test]
    public async Task Cleans_Archive_And_Extracted_Directory_After_Success_And_Validation_Failure()
    {
        var publisher = CreatePublisher();

        (await publisher.InstallAsync(SchemaPackage(), _pluginFolder, CancellationToken.None))
            .Succeeded.Should().BeTrue();
        (await publisher.InstallAsync(
            SchemaPackage(messageType: "demo.NotThere"), _pluginFolder, CancellationToken.None))
            .Succeeded.Should().BeFalse();

        var staging = Path.Combine(_pluginFolder, PluginPackagePaths.StagingFolderName);
        Directory.Exists(staging).Should().BeTrue();
        Directory.EnumerateFileSystemEntries(staging).Should().BeEmpty();
    }

    [Test]
    public async Task Cleans_Staging_After_The_Reader_Throws()
    {
        var publisher = CreatePublisher();

        var act = async () => await publisher.InstallAsync(
            new MemoryStream("not a zip"u8.ToArray()), _pluginFolder, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidDataException>();
        Directory.EnumerateFileSystemEntries(
            Path.Combine(_pluginFolder, PluginPackagePaths.StagingFolderName)).Should().BeEmpty();
    }

    [Test]
    public async Task Installs_A_First_Assembly_Into_The_Live_Path()
    {
        RequireAssemblyFixture();
        var session = new PluginInstallSession();
        var publisher = CreatePublisher(allowBinary: true, session: session);

        var outcome = await publisher.InstallAsync(
            PluginPackageTestData.AssemblyPackage("demoplugin", FixtureAssemblyPath),
            _pluginFolder,
            CancellationToken.None);

        outcome.Succeeded.Should().BeTrue(outcome.Error);
        outcome.InstallPath.Should().Be(Path.Combine(_pluginFolder, "demoplugin"));
        outcome.RequiresRestart.Should().BeTrue();
        File.Exists(Path.Combine(_pluginFolder, "demoplugin", "demoplugin.dll")).Should().BeTrue();
        session.TryGet("demoplugin", out var requiresRestart).Should().BeTrue();
        requiresRestart.Should().BeTrue();
    }

    [Test]
    public async Task Stages_An_Existing_Assembly_Upgrade_Under_Pending()
    {
        RequireAssemblyFixture();
        var publisher = CreatePublisher(allowBinary: true);

        (await publisher.InstallAsync(
            PluginPackageTestData.AssemblyPackage("demoplugin", FixtureAssemblyPath),
            _pluginFolder,
            CancellationToken.None)).Succeeded.Should().BeTrue();

        var outcome = await publisher.InstallAsync(
            PluginPackageTestData.AssemblyPackage("demoplugin", FixtureAssemblyPath, "2.0.0"),
            _pluginFolder,
            CancellationToken.None);

        outcome.Succeeded.Should().BeTrue(outcome.Error);
        outcome.InstallPath.Should().Be(Path.Combine(_pluginFolder, "demoplugin"));
        outcome.RequiresRestart.Should().BeTrue();
        File.ReadAllText(Path.Combine(
            _pluginFolder, PluginPackagePaths.PendingFolderName, "demoplugin", PluginManifestValidator.FileName))
            .Should().Contain("2.0.0");
    }

    [Test]
    public async Task Repeated_Assembly_Upgrades_Keep_Only_The_Latest_Pending_Package()
    {
        RequireAssemblyFixture();
        var publisher = CreatePublisher(allowBinary: true);
        await publisher.InstallAsync(
            PluginPackageTestData.AssemblyPackage("demoplugin", FixtureAssemblyPath),
            _pluginFolder,
            CancellationToken.None);

        await publisher.InstallAsync(
            PluginPackageTestData.AssemblyPackage("demoplugin", FixtureAssemblyPath, "2.0.0"),
            _pluginFolder,
            CancellationToken.None);
        await publisher.InstallAsync(
            PluginPackageTestData.AssemblyPackage("demoplugin", FixtureAssemblyPath, "3.0.0"),
            _pluginFolder,
            CancellationToken.None);

        var pendingRoot = Path.Combine(_pluginFolder, PluginPackagePaths.PendingFolderName);
        Directory.GetDirectories(pendingRoot).Should().ContainSingle();
        File.ReadAllText(Path.Combine(pendingRoot, "demoplugin", PluginManifestValidator.FileName))
            .Should().Contain("3.0.0");
    }

    [Test]
    public async Task Pending_Operations_Preserve_Remove_Then_Upgrade_Precedence()
    {
        RequireAssemblyFixture();
        var publisher = CreatePublisher(allowBinary: true);
        await publisher.InstallAsync(
            PluginPackageTestData.AssemblyPackage("demoplugin", FixtureAssemblyPath),
            _pluginFolder,
            CancellationToken.None);

        PluginPendingOperations.MarkForRemoval(_pluginFolder, "demoplugin");
        await publisher.InstallAsync(
            PluginPackageTestData.AssemblyPackage("demoplugin", FixtureAssemblyPath, "2.0.0"),
            _pluginFolder,
            CancellationToken.None);

        var applied = PluginPendingOperations.Apply([_pluginFolder], null);

        applied.Should().ContainSingle().Which.Should().Be("demoplugin");
        File.ReadAllText(Path.Combine(
            _pluginFolder, "demoplugin", PluginManifestValidator.FileName)).Should().Contain("2.0.0");
    }

    [Test]
    public async Task Pending_Operations_Preserve_Upgrade_Then_Remove_Precedence()
    {
        RequireAssemblyFixture();
        var publisher = CreatePublisher(allowBinary: true);
        await publisher.InstallAsync(
            PluginPackageTestData.AssemblyPackage("demoplugin", FixtureAssemblyPath),
            _pluginFolder,
            CancellationToken.None);
        await publisher.InstallAsync(
            PluginPackageTestData.AssemblyPackage("demoplugin", FixtureAssemblyPath, "2.0.0"),
            _pluginFolder,
            CancellationToken.None);

        PluginPendingOperations.MarkForRemoval(_pluginFolder, "demoplugin");
        PluginPendingOperations.Apply([_pluginFolder], null);

        Directory.Exists(Path.Combine(_pluginFolder, "demoplugin")).Should().BeFalse();
    }

    private static void RequireAssemblyFixture()
    {
        if (!PluginAssemblyInspector.AssemblyPluginsSupported)
        {
            Assert.Ignore("Assembly plugins are not supported on this platform.");
        }

        if (!File.Exists(FixtureAssemblyPath))
        {
            Assert.Ignore("MqttProbe.PluginLoader.Fixtures.dll was not built.");
        }
    }
}
