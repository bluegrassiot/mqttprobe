using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using MqttProbe.Models.Plugins;
using MqttProbe.Services.Platform;
using MqttProbe.Services.Plugins.Contracts;
using MqttProbe.Services.Plugins.Packaging;

namespace MqttProbe.Shared.Tests.Services.Plugins.Packaging;

[TestFixture]
public class PluginAssemblyPackageTests
{
    private string _pluginFolder = string.Empty;

    private static string TestOutputDir => NUnit.Framework.TestContext.CurrentContext.TestDirectory;

    // The fixtures assembly is a ProjectReference with ReferenceOutputAssembly="false"
    // (it must not be a compile-time reference of this test project, or its types would
    // be loaded into the test's own load context instead of a fresh MetadataLoadContext),
    // so it is located by the path the project's post-build Copy target places it at,
    // the same convention PluginLoaderTests uses.
    private static string FixtureAssemblyDir => Path.GetFullPath(Path.Combine(TestOutputDir, "PluginFixtures"));

    private static string FixtureAssemblyPath =>
        Path.Combine(FixtureAssemblyDir, "MqttProbe.PluginLoader.Fixtures.dll");

    [OneTimeSetUp]
    public void VerifyFixtureAssembly()
    {
        if (!File.Exists(FixtureAssemblyPath))
        {
            Assert.Ignore("MqttProbe.PluginLoader.Fixtures.dll not found; build the solution before running these tests.");
        }
    }

    [SetUp]
    public void SetUp()
    {
        _pluginFolder = Path.Combine(Path.GetTempPath(), "mqttprobe-asm-" + Guid.NewGuid().ToString("N"));
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

    private PluginPackageInstaller CreateInstaller(bool allowBinary)
    {
        var appInfo = Substitute.For<IAppInfoService>();
        appInfo.GetVersion().Returns("1.0.3");

        var config = new PluginConfig { AllowBinaryPackages = allowBinary };
        config.PluginFolders.Add(_pluginFolder);

        return new PluginPackageInstaller(
            config, appInfo, new PluginInstallSession(), new PluginArchiveLimits(), NullLoggerFactory.Instance);
    }

    private static string ContractAssemblyPath() => typeof(IMqttProbePlugin).Assembly.Location;

    private static MemoryStream AssemblyPackage(string id, string dllSourcePath, string version = "1.0.0") =>
        PluginPackageTestData.AssemblyPackage(id, dllSourcePath, version);

    [Test]
    public async Task Rejects_An_Assembly_Package_When_Binary_Packages_Are_Disabled()
    {
        var outcome = await CreateInstaller(allowBinary: false)
            .InstallAsync(AssemblyPackage("demoplugin", ContractAssemblyPath()), CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        outcome.Error.Should().Contain("AllowBinaryPackages");
    }

    [Test]
    public async Task Rejects_An_Assembly_Package_With_No_Plugin_Implementation()
    {
        if (!PluginAssemblyInspector.AssemblyPluginsSupported)
        {
            Assert.Ignore("Assembly plugins are not supported on this platform.");
        }

        // MqttProbe.Shared contains the IMqttProbePlugin interface but no concrete implementation.
        var outcome = await CreateInstaller(allowBinary: true)
            .InstallAsync(AssemblyPackage("demoplugin", ContractAssemblyPath()), CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        outcome.Error.Should().Contain("IMqttProbePlugin");
    }

    [Test]
    public async Task Rejects_An_Assembly_Package_Whose_Primary_Dll_Is_Misnamed()
    {
        var buffer = new MemoryStream();

        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry(PluginManifestValidator.FileName).Open(), Encoding.UTF8))
            {
                writer.Write(
                    """
                    { "id": "demoplugin", "name": "Demo", "version": "1.0.0", "kind": "assembly" }
                    """);
            }

            zip.CreateEntryFromFile(ContractAssemblyPath(), "something-else.dll");
        }

        buffer.Position = 0;

        var outcome = await CreateInstaller(allowBinary: true).InstallAsync(buffer, CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        outcome.Error.Should().Contain("demoplugin.dll");
    }

    [Test]
    public void Validation_Does_Not_Lock_The_Staged_File()
    {
        if (!PluginAssemblyInspector.AssemblyPluginsSupported)
        {
            Assert.Ignore("Assembly plugins are not supported on this platform.");
        }

        var staging = Path.Combine(_pluginFolder, "staged");
        Directory.CreateDirectory(staging);

        var copied = Path.Combine(staging, "demoplugin.dll");
        File.Copy(ContractAssemblyPath(), copied);

        PluginAssemblyInspector.Validate(staging, "demoplugin");

        var act = () => Directory.Delete(staging, recursive: true);

        act.Should().NotThrow("reflection-only inspection must not hold the DLL open");
    }

    // Hand-crafts minimal IL metadata (rather than compiling C#) so the assembly's one
    // type has a base type reference into an AssemblyRef ("Nonexistent.Fake.Dependency")
    // that can never resolve on any machine or CI runner - reproducing, deterministically,
    // the "dependency not found" failure real reflection over an uploaded assembly hit
    // during Task 4, without depending on any real third-party package being absent.
    private static void BuildAssemblyWithUnresolvableDependency(string outputPath)
    {
        var metadata = new MetadataBuilder();

        metadata.AddModule(
            generation: 0,
            moduleName: metadata.GetOrAddString(Path.GetFileName(outputPath)),
            mvid: metadata.GetOrAddGuid(Guid.NewGuid()),
            encId: default,
            encBaseId: default);

        metadata.AddAssembly(
            name: metadata.GetOrAddString(Path.GetFileNameWithoutExtension(outputPath)),
            version: new Version(1, 0, 0, 0),
            culture: default,
            publicKey: default,
            flags: 0,
            hashAlgorithm: AssemblyHashAlgorithm.None);

        var fakeAssemblyRef = metadata.AddAssemblyReference(
            name: metadata.GetOrAddString("Nonexistent.Fake.Dependency"),
            version: new Version(1, 0, 0, 0),
            culture: default,
            publicKeyOrToken: default,
            flags: 0,
            hashValue: default);

        var fakeBaseTypeRef = metadata.AddTypeReference(
            resolutionScope: fakeAssemblyRef,
            @namespace: metadata.GetOrAddString("FakeNamespace"),
            name: metadata.GetOrAddString("FakeBase"));

        metadata.AddTypeDefinition(
            attributes: default,
            @namespace: default,
            name: metadata.GetOrAddString("<Module>"),
            baseType: default,
            fieldList: MetadataTokens.FieldDefinitionHandle(1),
            methodList: MetadataTokens.MethodDefinitionHandle(1));

        metadata.AddTypeDefinition(
            attributes: TypeAttributes.Public | TypeAttributes.Class,
            @namespace: metadata.GetOrAddString(string.Empty),
            name: metadata.GetOrAddString("DemoPlugin"),
            baseType: fakeBaseTypeRef,
            fieldList: MetadataTokens.FieldDefinitionHandle(1),
            methodList: MetadataTokens.MethodDefinitionHandle(1));

        var peBuilder = new ManagedPEBuilder(
            new PEHeaderBuilder(imageCharacteristics: Characteristics.Dll | Characteristics.ExecutableImage),
            new MetadataRootBuilder(metadata),
            new BlobBuilder());

        var peBlob = new BlobBuilder();
        peBuilder.Serialize(peBlob);

        using (var file = new FileStream(outputPath, FileMode.Create))
        {
            peBlob.WriteContentTo(file);
        }

    }

    [Test]
    public async Task Rejects_An_Assembly_Package_Whose_Base_Type_Cannot_Be_Resolved()
    {
        if (!PluginAssemblyInspector.AssemblyPluginsSupported)
        {
            Assert.Ignore("Assembly plugins are not supported on this platform.");
        }

        var dllPath = Path.Combine(_pluginFolder, "demoplugin-source.dll");
        BuildAssemblyWithUnresolvableDependency(dllPath);

        var outcome = await CreateInstaller(allowBinary: true)
            .InstallAsync(AssemblyPackage("demoplugin", dllPath), CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        outcome.Error.Should().Contain("demoplugin.dll");
        outcome.Error.Should().Contain("Nonexistent.Fake.Dependency",
            "the message must name the unresolved dependency, not fall back to a generic install-failed error");
    }

    [Test]
    public async Task Installs_A_Valid_Assembly_Package_Into_The_Plugin_Folder()
    {
        if (!PluginAssemblyInspector.AssemblyPluginsSupported)
        {
            Assert.Ignore("Assembly plugins are not supported on this platform.");
        }

        var outcome = await CreateInstaller(allowBinary: true)
            .InstallAsync(AssemblyPackage("demoplugin", FixtureAssemblyPath), CancellationToken.None);

        outcome.Succeeded.Should().BeTrue(outcome.Error);
        outcome.RequiresRestart.Should().BeTrue("loading a new plugin assembly requires an app restart to take effect");

        var expected = Path.Combine(_pluginFolder, "demoplugin", "demoplugin.dll");
        File.Exists(expected).Should().BeTrue();
    }

    [Test]
    public async Task Upgrading_An_Installed_Assembly_Stages_It_Under_Pending_Instead_Of_Swapping_In_Place()
    {
        if (!PluginAssemblyInspector.AssemblyPluginsSupported)
        {
            Assert.Ignore("Assembly plugins are not supported on this platform.");
        }

        var installer = CreateInstaller(allowBinary: true);
        await installer.InstallAsync(AssemblyPackage("demoplugin", FixtureAssemblyPath), CancellationToken.None);

        var installPath = Path.Combine(_pluginFolder, "demoplugin");
        var installedDll = Path.Combine(installPath, "demoplugin.dll");
        var originalWriteTimeUtc = File.GetLastWriteTimeUtc(installedDll);

        var outcome = await installer.InstallAsync(
            AssemblyPackage("demoplugin", FixtureAssemblyPath), CancellationToken.None);

        outcome.Succeeded.Should().BeTrue(outcome.Error);
        outcome.RequiresRestart.Should().BeTrue();

        File.GetLastWriteTimeUtc(installedDll).Should().Be(originalWriteTimeUtc,
            "the running process may hold the loaded assembly open, so the live install must be untouched until restart");

        var pendingPath = Path.Combine(_pluginFolder, PluginPackagePaths.PendingFolderName, "demoplugin");
        File.Exists(Path.Combine(pendingPath, "demoplugin.dll"))
            .Should().BeTrue("the upgrade must be staged under .pending for PluginPendingOperations.Apply to pick up at next start");
    }

    [Test]
    public async Task Upgrading_An_Installed_Assembly_Still_Leaves_No_Staging_Directory_Behind()
    {
        if (!PluginAssemblyInspector.AssemblyPluginsSupported)
        {
            Assert.Ignore("Assembly plugins are not supported on this platform.");
        }

        var installer = CreateInstaller(allowBinary: true);
        await installer.InstallAsync(AssemblyPackage("demoplugin", FixtureAssemblyPath), CancellationToken.None);
        await installer.InstallAsync(AssemblyPackage("demoplugin", FixtureAssemblyPath), CancellationToken.None);

        var staging = Path.Combine(_pluginFolder, PluginPackagePaths.StagingFolderName);

        Directory.Exists(staging).Should().BeTrue("InstallAsync creates the staging root on every attempt");
        Directory.EnumerateFileSystemEntries(staging).Should().BeEmpty(
            "Swap moves the staged directory into .pending, so the extract-path cleanup in the finally block " +
            "becomes a no-op rather than leaving anything behind");
    }

    [Test]
    public async Task RemoveAsync_Defers_Removal_Of_An_Installed_Assembly_To_Next_Start()
    {
        if (!PluginAssemblyInspector.AssemblyPluginsSupported)
        {
            Assert.Ignore("Assembly plugins are not supported on this platform.");
        }

        var installer = CreateInstaller(allowBinary: true);
        await installer.InstallAsync(AssemblyPackage("demoplugin", FixtureAssemblyPath), CancellationToken.None);

        var installPath = Path.Combine(_pluginFolder, "demoplugin");

        var outcome = await installer.RemoveAsync("demoplugin", CancellationToken.None);

        outcome.Succeeded.Should().BeTrue(outcome.Error);
        outcome.RequiresRestart.Should().BeTrue("the running process may hold the loaded assembly open");
        Directory.Exists(installPath).Should().BeTrue("removal of a loaded assembly must be deferred, not immediate");
        PluginPendingOperations.HasPendingOperation(_pluginFolder, "demoplugin").Should().BeTrue();
    }

    [Test]
    public async Task Two_Consecutive_Deferred_Upgrades_For_The_Same_Id_Leave_One_Pending_Directory_With_Latest_Content()
    {
        if (!PluginAssemblyInspector.AssemblyPluginsSupported)
        {
            Assert.Ignore("Assembly plugins are not supported on this platform.");
        }

        var installer = CreateInstaller(allowBinary: true);
        await installer.InstallAsync(AssemblyPackage("demoplugin", FixtureAssemblyPath), CancellationToken.None);
        await installer.InstallAsync(AssemblyPackage("demoplugin", FixtureAssemblyPath, "2.0.0"), CancellationToken.None);
        var thirdOutcome = await installer.InstallAsync(
            AssemblyPackage("demoplugin", FixtureAssemblyPath, "3.0.0"), CancellationToken.None);

        thirdOutcome.Succeeded.Should().BeTrue(thirdOutcome.Error);

        var pendingRoot = Path.Combine(_pluginFolder, PluginPackagePaths.PendingFolderName);
        Directory.GetDirectories(pendingRoot).Should().HaveCount(1,
            "a second deferred upgrade for the same id must replace the first pending upgrade, not leak a backup alongside it");

        var pendingManifest = File.ReadAllText(
            Path.Combine(pendingRoot, "demoplugin", PluginManifestValidator.FileName));
        pendingManifest.Should().Contain("3.0.0");

        PluginPendingOperations.Apply([_pluginFolder], null);

        var installedManifest = File.ReadAllText(
            Path.Combine(_pluginFolder, "demoplugin", PluginManifestValidator.FileName));
        installedManifest.Should().Contain("3.0.0");
    }

    [Test]
    public async Task Remove_Then_Upgrade_For_The_Same_Id_Before_Restart_Applies_The_Upgrade()
    {
        if (!PluginAssemblyInspector.AssemblyPluginsSupported)
        {
            Assert.Ignore("Assembly plugins are not supported on this platform.");
        }

        var installer = CreateInstaller(allowBinary: true);
        await installer.InstallAsync(AssemblyPackage("demoplugin", FixtureAssemblyPath), CancellationToken.None);

        var removeOutcome = await installer.RemoveAsync("demoplugin", CancellationToken.None);
        removeOutcome.Succeeded.Should().BeTrue(removeOutcome.Error);

        var upgradeOutcome = await installer.InstallAsync(
            AssemblyPackage("demoplugin", FixtureAssemblyPath, "2.0.0"), CancellationToken.None);
        upgradeOutcome.Succeeded.Should().BeTrue(upgradeOutcome.Error);

        var applied = PluginPendingOperations.Apply([_pluginFolder], null);

        var installPath = Path.Combine(_pluginFolder, "demoplugin");
        Directory.Exists(installPath).Should().BeTrue("the later upgrade request must win over the earlier removal");
        File.ReadAllText(Path.Combine(installPath, PluginManifestValidator.FileName)).Should().Contain("2.0.0");
        applied.Count(x => x == "demoplugin").Should().Be(1,
            "the superseded removal must not also be reported as a separately applied operation");
    }

    [Test]
    public async Task Upgrade_Then_Remove_For_The_Same_Id_Before_Restart_Removes_The_Install()
    {
        if (!PluginAssemblyInspector.AssemblyPluginsSupported)
        {
            Assert.Ignore("Assembly plugins are not supported on this platform.");
        }

        var installer = CreateInstaller(allowBinary: true);
        await installer.InstallAsync(AssemblyPackage("demoplugin", FixtureAssemblyPath), CancellationToken.None);
        await installer.InstallAsync(AssemblyPackage("demoplugin", FixtureAssemblyPath, "2.0.0"), CancellationToken.None);

        var removeOutcome = await installer.RemoveAsync("demoplugin", CancellationToken.None);
        removeOutcome.Succeeded.Should().BeTrue(removeOutcome.Error);

        PluginPendingOperations.Apply([_pluginFolder], null);

        var installPath = Path.Combine(_pluginFolder, "demoplugin");
        Directory.Exists(installPath).Should().BeFalse("the later removal request must win over the earlier staged upgrade");
    }
}
