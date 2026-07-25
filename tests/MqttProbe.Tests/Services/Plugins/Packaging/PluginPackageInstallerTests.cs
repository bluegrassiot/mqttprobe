using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using MqttProbe.Models.Plugins;
using MqttProbe.Services.Platform;
using MqttProbe.Services.Plugins.Packaging;
using MqttProbe.Services.Plugins.Protobuf;
using NSubstitute;

namespace MqttProbe.Tests.Services.Plugins.Packaging;

[TestFixture]
public class PluginPackageInstallerTests
{
    private string _pluginFolder = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _pluginFolder = Path.Combine(Path.GetTempPath(), "mqttprobe-install-" + Guid.NewGuid().ToString("N"));
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

    private PluginPackageInstaller CreateInstaller(bool allowBinary = false)
    {
        var appInfo = Substitute.For<IAppInfoService>();
        appInfo.GetVersion().Returns("1.0.3");

        var config = new PluginConfig { AllowBinaryPackages = allowBinary };
        config.PluginFolders.Add(_pluginFolder);

        return new PluginPackageInstaller(
            config,
            appInfo,
            new PluginInstallSession(),
            new PluginArchiveLimits(),
            NullLoggerFactory.Instance);
    }

    private const string DemoProto = """
        syntax = "proto3";
        package demo;
        message Reading { int32 id = 1; string label = 2; }
        """;

    private static string PluginManifestJson(string id, string version) =>
        $$"""
        {
          "id": "{{id}}",
          "name": "Demo Schemas",
          "version": "{{version}}",
          "kind": "protobuf-schemas"
        }
        """;

    private static string SchemaManifestJson(string messageType) =>
        $$"""
        {
          "schemas": [
            {
              "files": [ "demo.proto" ],
              "topicPattern": "demo/+/reading",
              "messageType": "{{messageType}}"
            }
          ]
        }
        """;

    private static MemoryStream SchemaPackage(
        string id = "demo",
        string version = "1.0.0",
        string messageType = "demo.Reading",
        int paddingBytes = 0)
    {
        var buffer = new MemoryStream();

        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, PluginManifestValidator.FileName, PluginManifestJson(id, version));
            Write(zip, ProtobufSchemaFolderLoader.ManifestFileName, SchemaManifestJson(messageType));

            // Random (not repetitive) padding so it survives Deflate and actually
            // pushes the archive's on-disk size past a low MaxCompressedBytes cap.
            var proto = paddingBytes > 0
                ? DemoProto + "\n// " + Convert.ToBase64String(RandomNumberGenerator.GetBytes(paddingBytes))
                : DemoProto;

            Write(zip, "demo.proto", proto);
        }

        buffer.Position = 0;
        return buffer;
    }

    private static void Write(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8);
        writer.Write(content);
    }

    private static void WriteSchemaFilesToDisk(string root, string id = "demo", string version = "1.0.0")
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, PluginManifestValidator.FileName), PluginManifestJson(id, version));
        File.WriteAllText(
            Path.Combine(root, ProtobufSchemaFolderLoader.ManifestFileName), SchemaManifestJson("demo.Reading"));
        File.WriteAllText(Path.Combine(root, "demo.proto"), DemoProto);
    }

    // Builds a valid schema package plus one extra entry ("padding.txt") whose
    // central-directory uncompressed-size field is patched to a tiny lie after
    // the fact, while the real compressed data (and its true expansion) is left
    // untouched - the same "declared tiny, actual huge" shape a real zip bomb
    // uses to slip past length-based validation that only trusts the header.
    private static MemoryStream SchemaPackageWithFalsifiedPaddingLength(int realPaddingBytes)
    {
        var buffer = new MemoryStream();

        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, PluginManifestValidator.FileName, PluginManifestJson("demo", "1.0.0"));
            Write(zip, ProtobufSchemaFolderLoader.ManifestFileName, SchemaManifestJson("demo.Reading"));
            Write(zip, "demo.proto", DemoProto);

            var entry = zip.CreateEntry("padding.txt", CompressionLevel.NoCompression);
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(Convert.ToBase64String(RandomNumberGenerator.GetBytes(realPaddingBytes)));
        }

        var bytes = buffer.ToArray();
        PatchCentralDirectoryUncompressedSize(bytes, "padding.txt", declaredSize: 1);

        return new MemoryStream(bytes);
    }

    private static void PatchCentralDirectoryUncompressedSize(byte[] zipBytes, string entryName, uint declaredSize)
    {
        var nameBytes = Encoding.UTF8.GetBytes(entryName);
        var signature = new byte[] { 0x50, 0x4B, 0x01, 0x02 };

        for (var i = 0; i + 46 <= zipBytes.Length; i++)
        {
            if (zipBytes[i] != signature[0] || zipBytes[i + 1] != signature[1]
                || zipBytes[i + 2] != signature[2] || zipBytes[i + 3] != signature[3])
            {
                continue;
            }

            var nameLength = BitConverter.ToUInt16(zipBytes, i + 28);

            if (nameLength != nameBytes.Length
                || !zipBytes.AsSpan(i + 46, nameLength).SequenceEqual(nameBytes))
            {
                continue;
            }

            BitConverter.GetBytes(declaredSize).CopyTo(zipBytes, i + 24);
            return;
        }

        throw new InvalidOperationException($"Central directory entry for '{entryName}' not found.");
    }

    [Test]
    public async Task Installs_A_Valid_Schema_Bundle_Into_The_Protobuf_Folder()
    {
        var outcome = await CreateInstaller().InstallAsync(SchemaPackage(), CancellationToken.None);

        outcome.Succeeded.Should().BeTrue(outcome.Error);
        outcome.RequiresRestart.Should().BeFalse("schema bundles activate without a restart");

        var expected = Path.Combine(_pluginFolder, ProtobufSchemaFolderLoader.FolderName, "demo");
        outcome.InstallPath.Should().Be(expected);
        File.Exists(Path.Combine(expected, "demo.proto")).Should().BeTrue();
        File.Exists(Path.Combine(expected, PluginManifestValidator.FileName)).Should().BeTrue();
    }

    [Test]
    public async Task Installed_Bundle_Is_Discovered_By_The_Existing_Loader()
    {
        await CreateInstaller().InstallAsync(SchemaPackage(), CancellationToken.None);

        var sources = ProtobufSchemaFolderLoader.Discover([_pluginFolder]);

        sources.Should().ContainSingle();
        new ProtobufSchemaRegistry(sources).HasAnySchemas.Should().BeTrue();
    }

    [Test]
    public async Task Rejects_A_Bundle_Whose_Declared_Message_Type_Does_Not_Exist()
    {
        var outcome = await CreateInstaller()
            .InstallAsync(SchemaPackage(messageType: "demo.NotThere"), CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        outcome.Error.Should().Contain("demo.NotThere");

        Directory.Exists(Path.Combine(_pluginFolder, ProtobufSchemaFolderLoader.FolderName, "demo"))
            .Should().BeFalse("a package that fails validation must never be published");
    }

    [Test]
    public async Task Leaves_No_Staging_Directory_Behind_After_Success_Or_Failure()
    {
        var installer = CreateInstaller();

        await installer.InstallAsync(SchemaPackage(), CancellationToken.None);
        await installer.InstallAsync(SchemaPackage(messageType: "demo.NotThere"), CancellationToken.None);

        var staging = Path.Combine(_pluginFolder, PluginPackagePaths.StagingFolderName);

        Directory.Exists(staging).Should().BeTrue("InstallAsync creates the staging root on every attempt");
        Directory.EnumerateFileSystemEntries(staging).Should().BeEmpty();
    }

    [Test]
    public async Task Upgrading_Replaces_The_Previous_Contents()
    {
        var installer = CreateInstaller();
        await installer.InstallAsync(SchemaPackage(version: "1.0.0"), CancellationToken.None);

        var installPath = Path.Combine(_pluginFolder, ProtobufSchemaFolderLoader.FolderName, "demo");
        File.WriteAllText(Path.Combine(installPath, "stale.txt"), "left over");

        var outcome = await installer.InstallAsync(SchemaPackage(version: "1.1.0"), CancellationToken.None);

        outcome.Succeeded.Should().BeTrue(outcome.Error);
        outcome.Manifest!.Version.Should().Be("1.1.0");
        File.Exists(Path.Combine(installPath, "stale.txt"))
            .Should().BeFalse("upgrade replaces the directory rather than merging into it");
    }

    [Test]
    public async Task A_Failed_Upgrade_Restores_The_Previous_Install()
    {
        var installer = CreateInstaller();
        await installer.InstallAsync(SchemaPackage(version: "1.0.0"), CancellationToken.None);

        var outcome = await installer.InstallAsync(
            SchemaPackage(version: "1.1.0", messageType: "demo.NotThere"), CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();

        var installPath = Path.Combine(_pluginFolder, ProtobufSchemaFolderLoader.FolderName, "demo");
        File.Exists(Path.Combine(installPath, "demo.proto"))
            .Should().BeTrue("the working install must survive a failed upgrade");
    }

    [Test]
    public void A_Failed_Forward_Move_Restores_The_Previous_Install_Intact()
    {
        var installer = CreateInstaller();

        var installPath = Path.Combine(_pluginFolder, ProtobufSchemaFolderLoader.FolderName, "demo");
        WriteSchemaFilesToDisk(installPath, version: "1.0.0");

        // A staged path that doesn't exist makes the forward Directory.Move throw
        // DirectoryNotFoundException (an IOException) deterministically on every
        // platform, at exactly the step Swap's rollback exists to protect against -
        // unlike a locked file, which Unix rename(2) would ignore.
        var stagedPath = Path.Combine(_pluginFolder, PluginPackagePaths.StagingFolderName, Guid.NewGuid().ToString("N"));

        var act = () => installer.Swap(stagedPath, installPath);
        act.Should().Throw<IOException>();

        Directory.Exists(installPath).Should().BeTrue("a failed upgrade must not destroy the working install");
        File.ReadAllText(Path.Combine(installPath, PluginManifestValidator.FileName))
            .Should().Contain("1.0.0", "the restored install must be the previous version, not a half-applied upgrade");
    }

    [Test]
    public async Task Rejects_A_Package_Whose_Compressed_Size_Exceeds_The_Configured_Cap()
    {
        var appInfo = Substitute.For<IAppInfoService>();
        appInfo.GetVersion().Returns("1.0.3");

        var config = new PluginConfig();
        config.PluginFolders.Add(_pluginFolder);

        var installer = new PluginPackageInstaller(
            config,
            appInfo,
            new PluginInstallSession(),
            new PluginArchiveLimits { MaxCompressedBytes = 512 },
            NullLoggerFactory.Instance);

        using var package = SchemaPackage(paddingBytes: 4096);
        package.Length.Should().BeGreaterThan(512,
            "the test package must exceed the configured cap for this assertion to be meaningful");

        var outcome = await installer.InstallAsync(package, CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        outcome.Error.Should().Contain("512");

        Directory.Exists(Path.Combine(_pluginFolder, ProtobufSchemaFolderLoader.FolderName, "demo"))
            .Should().BeFalse("a package over the compressed-size cap must never be published");
    }

    [Test]
    public async Task Aborts_Extraction_When_Real_Bytes_Exceed_The_Uncompressed_Limit_Despite_A_Falsified_Declared_Length()
    {
        var appInfo = Substitute.For<IAppInfoService>();
        appInfo.GetVersion().Returns("1.0.3");

        var config = new PluginConfig();
        config.PluginFolders.Add(_pluginFolder);

        var installer = new PluginPackageInstaller(
            config,
            appInfo,
            new PluginInstallSession(),
            // MaxCompressedBytes is pinned too, so a future drop in its default
            // can't make the compressed cap trip first and pass this test for the wrong reason.
            new PluginArchiveLimits { MaxUncompressedBytes = 1_000, MaxCompressedBytes = 33_554_432 },
            NullLoggerFactory.Instance);

        using var package = SchemaPackageWithFalsifiedPaddingLength(realPaddingBytes: 200_000);

        var outcome = await installer.InstallAsync(package, CancellationToken.None);

        outcome.Succeeded.Should().BeFalse(
            "padding.txt declares a 1-byte expansion but really expands past the limit; extraction must still stop it");
        outcome.Error.Should().Contain("1000",
            "the specific expansion-limit message must reach the caller, not the generic install-failed fallback");

        Directory.Exists(Path.Combine(_pluginFolder, ProtobufSchemaFolderLoader.FolderName, "demo"))
            .Should().BeFalse("a package whose real expansion exceeds the limit must never be published");
    }

    [Test]
    public void A_Leaked_Upgrade_Backup_Is_Not_Discovered_As_A_Live_Package()
    {
        var installPath = Path.Combine(_pluginFolder, ProtobufSchemaFolderLoader.FolderName, "demo");
        WriteSchemaFilesToDisk(installPath, version: "1.0.0");

        // Simulate a backup Swap's own cleanup failed to remove, using its exact
        // naming scheme, without needing to force a real platform-specific delete failure.
        var backupPath = Path.Combine(
            Path.GetDirectoryName(installPath)!, PluginPackagePaths.BackupDirectoryName(installPath));
        WriteSchemaFilesToDisk(backupPath, version: "0.9.0");

        var sources = ProtobufSchemaFolderLoader.Discover([_pluginFolder]);

        sources.Should().ContainSingle("a leaked upgrade backup must never be treated as a live schema package");
        sources[0].SchemaRoot.Should().Be(installPath);

        PluginPackagePaths.IsReservedDirectoryName(Path.GetFileName(backupPath))
            .Should().BeTrue("Swap relies on this guard to hide any backup it fails to clean up");
    }

    [Test]
    public async Task Rejects_A_Package_With_No_Manifest()
    {
        var buffer = new MemoryStream();

        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, "demo.proto", DemoProto);
        }

        buffer.Position = 0;

        var outcome = await CreateInstaller().InstallAsync(buffer, CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        outcome.Error.Should().Contain(PluginManifestValidator.FileName);
    }

    [Test]
    public async Task Rejects_Content_That_Is_Not_A_Zip_Archive()
    {
        var outcome = await CreateInstaller()
            .InstallAsync(new MemoryStream("not a zip"u8.ToArray()), CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        outcome.Error.Should().Contain("archive");
    }

    [Test]
    public async Task Returns_A_Failure_Outcome_Rather_Than_Throwing_When_A_Configured_Folder_Path_Is_Invalid()
    {
        var appInfo = Substitute.For<IAppInfoService>();
        appInfo.GetVersion().Returns("1.0.3");

        var config = new PluginConfig();
        config.PluginFolders.Add(string.Empty);

        var installer = new PluginPackageInstaller(
            config,
            appInfo,
            new PluginInstallSession(),
            new PluginArchiveLimits(),
            NullLoggerFactory.Instance);

        var act = async () => await installer.InstallAsync(SchemaPackage(), CancellationToken.None);

        await act.Should().NotThrowAsync(
            "an empty configured plugin folder throws ArgumentException from Directory.CreateDirectory, " +
            "which InstallAsync must still turn into a failure outcome");
    }

    [Test]
    public async Task Canceled_Install_Propagates_Cancellation_Instead_Of_A_Generic_Failure_Outcome()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await CreateInstaller().InstallAsync(SchemaPackage(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public void Skips_A_Malformed_Folder_Entry_And_Returns_The_Next_Valid_One()
    {
        var appInfo = Substitute.For<IAppInfoService>();
        appInfo.GetVersion().Returns("1.0.3");

        var config = new PluginConfig();
        config.PluginFolders.Add(string.Empty);
        config.PluginFolders.Add(_pluginFolder);

        var installer = new PluginPackageInstaller(
            config,
            appInfo,
            new PluginInstallSession(),
            new PluginArchiveLimits(),
            NullLoggerFactory.Instance);

        installer.WritablePluginFolder.Should().Be(_pluginFolder,
            "an empty entry throws ArgumentException from Directory.CreateDirectory; the scan must skip it, not abort");
    }

    [Test]
    public async Task RemoveAsync_Deletes_An_Installed_Schema_Bundle_Immediately()
    {
        var installer = CreateInstaller();
        await installer.InstallAsync(SchemaPackage(), CancellationToken.None);

        var installPath = Path.Combine(_pluginFolder, ProtobufSchemaFolderLoader.FolderName, "demo");
        Directory.Exists(installPath).Should().BeTrue();

        var outcome = await installer.RemoveAsync("demo", CancellationToken.None);

        outcome.Succeeded.Should().BeTrue(outcome.Error);
        outcome.RequiresRestart.Should().BeFalse("a schema bundle is a plain file with nothing holding it open");
        Directory.Exists(installPath).Should().BeFalse();
        PluginPendingOperations.HasPendingOperation(_pluginFolder, "demo").Should().BeFalse();
    }

    [Test]
    public async Task RemoveAsync_Fails_For_A_Plugin_That_Is_Not_Installed()
    {
        var outcome = await CreateInstaller().RemoveAsync("not-installed", CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        outcome.Error.Should().Contain("not-installed");
    }

    [Test]
    public async Task RemoveAsync_Returns_A_Failure_Outcome_Rather_Than_Throwing_When_The_Pending_Area_Cannot_Be_Cleared()
    {
        var installer = CreateInstaller(allowBinary: true);

        var assemblyPath = Path.Combine(_pluginFolder, "demoplugin");
        Directory.CreateDirectory(assemblyPath);
        File.WriteAllText(Path.Combine(assemblyPath, "demoplugin.dll"), "MZ");

        // A file occupying the .pending path makes MarkForRemoval's Directory.CreateDirectory
        // throw IOException deterministically on every platform - no locked handle required.
        File.WriteAllText(Path.Combine(_pluginFolder, PluginPackagePaths.PendingFolderName), string.Empty);

        var act = async () => await installer.RemoveAsync("demoplugin", CancellationToken.None);

        var outcome = await act.Should().NotThrowAsync();
        outcome.Subject.Succeeded.Should().BeFalse();
        outcome.Subject.Error.Should().NotContain(_pluginFolder, "the failure message must not leak an absolute server path");
    }

    [Test]
    public async Task RemoveAsync_By_Id_Alone_Cannot_Reach_A_Package_Outside_The_First_Writable_Folder()
    {
        // Documents the current, still-preserved id-only overload's blind spot: it only ever
        // looks under WritablePluginFolder (the *first* writable entry), so a package that
        // lives under a later configured folder reads as "not installed" even though the
        // inventory panel is listing it.
        var firstFolder = Path.Combine(Path.GetTempPath(), "mqttprobe-install-first-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(firstFolder);

        var config = new PluginConfig();
        config.PluginFolders.Add(firstFolder);
        config.PluginFolders.Add(_pluginFolder);

        var appInfo = Substitute.For<IAppInfoService>();
        appInfo.GetVersion().Returns("1.0.3");

        var installer = new PluginPackageInstaller(
            config, appInfo, new PluginInstallSession(), new PluginArchiveLimits(), NullLoggerFactory.Instance);

        var installPath = Path.Combine(_pluginFolder, ProtobufSchemaFolderLoader.FolderName, "demo");
        WriteSchemaFilesToDisk(installPath);

        var outcome = await installer.RemoveAsync("demo", CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        Directory.Exists(installPath).Should().BeTrue("the id-only overload must not have touched it");

        Directory.Delete(firstFolder, recursive: true);
    }

    [Test]
    public async Task RemoveAsync_With_InstallPath_Removes_A_Schema_Package_From_A_NonFirst_Plugin_Folder()
    {
        var firstFolder = Path.Combine(Path.GetTempPath(), "mqttprobe-install-first-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(firstFolder);

        var config = new PluginConfig();
        config.PluginFolders.Add(firstFolder);
        config.PluginFolders.Add(_pluginFolder);

        var appInfo = Substitute.For<IAppInfoService>();
        appInfo.GetVersion().Returns("1.0.3");

        var installer = new PluginPackageInstaller(
            config, appInfo, new PluginInstallSession(), new PluginArchiveLimits(), NullLoggerFactory.Instance);

        var installPath = Path.Combine(_pluginFolder, ProtobufSchemaFolderLoader.FolderName, "demo");
        WriteSchemaFilesToDisk(installPath);

        var outcome = await installer.RemoveAsync("demo", installPath, CancellationToken.None);

        outcome.Succeeded.Should().BeTrue(outcome.Error);
        Directory.Exists(installPath).Should().BeFalse();

        Directory.Delete(firstFolder, recursive: true);
    }

    [Test]
    public async Task RemoveAsync_With_InstallPath_Defers_Removal_Of_An_Assembly_In_A_NonFirst_Plugin_Folder()
    {
        var firstFolder = Path.Combine(Path.GetTempPath(), "mqttprobe-install-first-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(firstFolder);

        var config = new PluginConfig { AllowBinaryPackages = true };
        config.PluginFolders.Add(firstFolder);
        config.PluginFolders.Add(_pluginFolder);

        var appInfo = Substitute.For<IAppInfoService>();
        appInfo.GetVersion().Returns("1.0.3");

        var installer = new PluginPackageInstaller(
            config, appInfo, new PluginInstallSession(), new PluginArchiveLimits(), NullLoggerFactory.Instance);

        var installPath = Path.Combine(_pluginFolder, "demoplugin");
        Directory.CreateDirectory(installPath);
        File.WriteAllText(Path.Combine(installPath, "demoplugin.dll"), "MZ");

        var outcome = await installer.RemoveAsync("demoplugin", installPath, CancellationToken.None);

        outcome.Succeeded.Should().BeTrue(outcome.Error);
        outcome.RequiresRestart.Should().BeTrue("a loaded assembly still needs the .pending marker, not a direct delete");
        PluginPendingOperations.HasPendingOperation(_pluginFolder, "demoplugin").Should().BeTrue(
            "the marker must be written to the folder that actually owns the package");
        PluginPendingOperations.HasPendingOperation(firstFolder, "demoplugin").Should().BeFalse();

        Directory.Delete(firstFolder, recursive: true);
    }

    [Test]
    public async Task RemoveAsync_With_InstallPath_Fails_With_A_Specific_Message_When_The_Owning_Folder_Is_Not_Writable()
    {
        var installer = CreateInstaller(allowBinary: true);
        installer.WritableProbeOverride = _ => false;

        var installPath = Path.Combine(_pluginFolder, "demoplugin");
        Directory.CreateDirectory(installPath);
        File.WriteAllText(Path.Combine(installPath, "demoplugin.dll"), "MZ");

        var outcome = await installer.RemoveAsync("demoplugin", installPath, CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        outcome.Error.Should().Contain("not writable");
        outcome.Error.Should().NotContain("not installed",
            "the package IS installed; the failure is about the folder, not a missing package");
    }

    [Test]
    public void Reports_No_Writable_Folder_When_None_Is_Configured()
    {
        var appInfo = Substitute.For<IAppInfoService>();
        appInfo.GetVersion().Returns("1.0.3");

        var installer = new PluginPackageInstaller(
            new PluginConfig(),
            appInfo,
            new PluginInstallSession(),
            new PluginArchiveLimits(),
            NullLoggerFactory.Instance);

        installer.WritablePluginFolder.Should().BeNull();
    }
}
