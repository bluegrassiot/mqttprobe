using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MqttProbe.Core.Models.Plugins;
using MqttProbe.Core.Services.Plugins;
using MqttProbe.Core.Services.Plugins.Loading;

namespace MqttProbe.Core.Tests.Services.Plugins.Loading;

[TestFixture]
public class PluginLoaderTests
{
    private static string TestOutputDir => NUnit.Framework.TestContext.CurrentContext.TestDirectory;

    private static string FixtureAssemblyDir => Path.GetFullPath(
        Path.Combine(TestOutputDir, "PluginFixtures"));

    private static ILogger<PluginLoader> NullLogger =>
        NullLogger<PluginLoader>.Instance;

    [OneTimeSetUp]
    public void VerifyFixtureAssembly()
    {
        var fixturePath = Path.Combine(FixtureAssemblyDir, "MqttProbe.PluginLoader.Fixtures.dll");

        if (!File.Exists(fixturePath))
        {
            Assert.Ignore(
                "MqttProbe.PluginLoader.Fixtures.dll not found; build the solution before running loader integration tests.");
        }
    }

    // ── Folder scanning ──────────────────────────────────────────────────────

    [Test]
    public void LoadPlugins_NonexistentFolder_ReturnsNoPluginsAndInfoDiagnostic()
    {
        var config = new PluginConfig
        {
            PluginFolders = [Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())]
        };
        var loader = new PluginLoader(config, NullLogger);

        var result = loader.LoadPlugins();

        result.Plugins.Should().BeEmpty();
        result.Diagnostics.Should().Contain(d =>
            d.Source == "loader" &&
            d.Severity == DiagnosticSeverity.Info &&
            d.Message.Contains("not found"));
    }

    [Test]
    public void LoadPlugins_EmptyFolder_ReturnsNoPluginsAndInfoDiagnostic()
    {
        var emptyDir = Path.Combine(Path.GetTempPath(), $"empty-{Guid.NewGuid()}");
        Directory.CreateDirectory(emptyDir);

        try
        {
            var config = new PluginConfig
            {
                PluginFolders = [emptyDir]
            };
            var loader = new PluginLoader(config, NullLogger);

            var result = loader.LoadPlugins();

            result.Plugins.Should().BeEmpty();
            result.Diagnostics.Should().Contain(d =>
                d.Source == "loader" &&
                d.Severity == DiagnosticSeverity.Info &&
                d.Message.Contains("No DLLs"));
        }
        finally
        {
            Directory.Delete(emptyDir, recursive: true);
        }
    }

    [Test]
    public void LoadPlugins_NoFoldersConfigured_ReturnsEmptyResult()
    {
        var config = new PluginConfig();
        var loader = new PluginLoader(config, NullLogger);

        var result = loader.LoadPlugins();

        result.Plugins.Should().BeEmpty();
        result.Diagnostics.Should().BeEmpty();
    }

    [Test]
    public void LoadPlugins_MultipleFolders_ScansAllFolders()
    {
        var config = new PluginConfig
        {
            PluginFolders = [FixtureAssemblyDir, TestOutputDir]
        };
        var loader = new PluginLoader(config, NullLogger);

        var result = loader.LoadPlugins();

        result.Plugins.Should().Contain(p => p.PluginId == "fixture-valid");

        result.Diagnostics.Should().Contain(d =>
            d.Message.Contains("No IMqttProbePlugin"));
    }

    // ── Type discovery / instantiation ───────────────────────────────────────

    [Test]
    public void LoadPlugins_AssemblyWithNoPluginImplementation_SkippedWithInfoDiagnostic()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"no-plugin-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // Copy a DLL that has no IMqttProbePlugin implementations.
            var srcDll = typeof(PluginLoaderTests).Assembly.Location;
            File.Copy(srcDll, Path.Combine(tempDir, Path.GetFileName(srcDll)));

            var config = new PluginConfig
            {
                PluginFolders = [tempDir]
            };
            var loader = new PluginLoader(config, NullLogger);

            var result = loader.LoadPlugins();

            result.Diagnostics.Should().Contain(d =>
                d.Source == "loader" &&
                d.Severity == DiagnosticSeverity.Info &&
                d.Message.Contains("No IMqttProbePlugin"));

            result.Plugins.Should().BeEmpty();
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    [Test]
    public void LoadPlugins_ValidPlugin_LoadsSuccessfully()
    {
        var config = new PluginConfig
        {
            PluginFolders = [FixtureAssemblyDir]
        };
        var loader = new PluginLoader(config, NullLogger);

        var result = loader.LoadPlugins();

        result.Plugins.Should().Contain(p => p.PluginId == "fixture-valid");
        result.Diagnostics.Should().NotContain(d =>
            d.Source == "fixture-valid" &&
            d.Severity == DiagnosticSeverity.Error);
    }

    [Test]
    public void LoadPlugins_ConstructorThrowingPlugin_RecordsErrorDiagnosticAndContinues()
    {
        // ThrowingPlugin throws in its constructor.  The loader must record an
        // Error diagnostic for the failing type and still return the other
        // plugin from the same assembly.
        var config = new PluginConfig
        {
            PluginFolders = [FixtureAssemblyDir]
        };
        var loader = new PluginLoader(config, NullLogger);

        var result = loader.LoadPlugins();

        result.Plugins.Should().Contain(p => p.PluginId == "fixture-valid");

        result.Diagnostics.Should().Contain(d =>
            d.Source.Contains("ThrowingPlugin") &&
            d.Severity == DiagnosticSeverity.Error &&
            d.Message.Contains("Failed to instantiate"));
    }

    // ── Disabled plugin IDs ──────────────────────────────────────────────────

    [Test]
    public void LoadPlugins_DisabledPluginId_SkippedWithInfoDiagnostic()
    {
        // fixture-valid is disabled → skipped after instantiation with info
        // diagnostic.  ThrowingPlugin fails at construction → error diagnostic.
        var config = new PluginConfig
        {
            PluginFolders = [FixtureAssemblyDir],
            DisabledPluginIds = ["fixture-valid"]
        };
        var loader = new PluginLoader(config, NullLogger);

        var result = loader.LoadPlugins();

        result.Plugins.Should().NotContain(p => p.PluginId == "fixture-valid");
        result.Diagnostics.Should().Contain(d =>
            d.Source == "fixture-valid" &&
            d.Severity == DiagnosticSeverity.Info &&
            d.Message.Contains("disabled"));
    }

    [Test]
    public void LoadPlugins_DisabledPluginId_CasingMismatch_StillSkipsAndReportsTheId()
    {
        // The fixture's PluginId is "fixture-valid". Hand-edited config is the only way to
        // populate this list, so a casing slip must not silently leave the plugin enabled.
        var config = new PluginConfig
        {
            PluginFolders = [FixtureAssemblyDir],
            DisabledPluginIds = ["Fixture-Valid"]
        };
        var loader = new PluginLoader(config, NullLogger);

        var result = loader.LoadPlugins();

        result.Plugins.Should().NotContain(p => p.PluginId == "fixture-valid");
        result.DisabledIds.Should().Contain("fixture-valid");
    }

    [Test]
    public void LoadPlugins_DisabledPlugin_OtherPluginsStillLoad()
    {
        // ThrowingPlugin is disabled → skipped after instantiation (but before
        // the disabled check the constructor already threw, so the disabled
        // diagnostic is not recorded for it; the error diagnostic is).
        // fixture-valid is not disabled → loads normally.
        var config = new PluginConfig
        {
            PluginFolders = [FixtureAssemblyDir],
            DisabledPluginIds = ["fixture-throwing"]
        };
        var loader = new PluginLoader(config, NullLogger);

        var result = loader.LoadPlugins();

        result.Plugins.Should().ContainSingle()
            .Which.PluginId.Should().Be("fixture-valid");

        // The disabled check runs after instantiation, so for ThrowingPlugin
        // (which throws in the constructor) the error diagnostic is recorded
        // instead of the disabled diagnostic.
        result.Diagnostics.Should().Contain(d =>
            d.Source.Contains("ThrowingPlugin") &&
            d.Severity == DiagnosticSeverity.Error);
    }

    // ── AssemblyLoadContext allowlist ─────────────────────────────────────────

    [Test]
    public void PluginLoadContext_SharedAssemblyNames_ContainsPluginContracts()
    {
        PluginLoadContext.SharedAssemblyNames.Should().Contain("MqttProbe.PluginContracts");
    }

    [Test]
    public void PluginLoadContext_IsSharedAssembly_ReturnsTrueForPluginContracts()
    {
        PluginLoadContext.IsSharedAssembly("MqttProbe.PluginContracts").Should().BeTrue();
    }

    [Test]
    public void PluginLoadContext_IsSharedAssembly_ReturnsFalseForNonSharedName()
    {
        PluginLoadContext.IsSharedAssembly("SomeThirdPartyLib").Should().BeFalse();
        PluginLoadContext.IsSharedAssembly(null).Should().BeFalse();
        PluginLoadContext.IsSharedAssembly(string.Empty).Should().BeFalse();
    }

    [Test]
    public void LoadPlugins_LoadedPlugin_ContractTypeSharesHostAssembly()
    {
        var config = new PluginConfig
        {
            PluginFolders = [FixtureAssemblyDir]
        };
        var loader = new PluginLoader(config, NullLogger);

        var result = loader.LoadPlugins();

        var plugin = result.Plugins.Should().ContainSingle(p => p.PluginId == "fixture-valid").Subject;

        // The IMqttProbePlugin interface on the loaded plugin must resolve to the same
        // assembly the host uses, proving the ALC shared-assembly identity contract.
        var hostAssembly = typeof(MqttProbe.PluginContracts.IMqttProbePlugin).Assembly;
        var pluginInterfaceAssembly = plugin.GetType()
            .GetInterfaces()
            .First(i => i.Name == "IMqttProbePlugin")
            .Assembly;

        pluginInterfaceAssembly.Should().BeSameAs(hostAssembly);
    }

    // ── Subdirectory scanning ─────────────────────────────────────────────────

    [Test]
    public void LoadPlugins_SubdirectoryLayout_LoadsPluginFromNestedDll()
    {
        var root = Path.Combine(Path.GetTempPath(), $"plugin-subdir-{Guid.NewGuid()}");
        var subDir = Path.Combine(root, "MyPlugin");
        Directory.CreateDirectory(subDir);

        try
        {
            var srcDll = Path.Combine(FixtureAssemblyDir, "MqttProbe.PluginLoader.Fixtures.dll");
            var destDll = Path.Combine(subDir, "MyPlugin.dll");
            File.Copy(srcDll, destDll);

            var config = new PluginConfig { PluginFolders = [root] };
            var loader = new PluginLoader(config, NullLogger);

            var result = loader.LoadPlugins();

            result.Plugins.Should().Contain(p => p.PluginId == "fixture-valid");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Test]
    public void LoadPlugins_SubdirectoryWithoutMatchingName_FallsBackToAllDlls()
    {
        var root = Path.Combine(Path.GetTempPath(), $"plugin-subdir2-{Guid.NewGuid()}");
        var subDir = Path.Combine(root, "SomeFolder");
        Directory.CreateDirectory(subDir);

        try
        {
            var srcDll = Path.Combine(FixtureAssemblyDir, "MqttProbe.PluginLoader.Fixtures.dll");
            var destDll = Path.Combine(subDir, "MqttProbe.PluginLoader.Fixtures.dll");
            File.Copy(srcDll, destDll);

            var config = new PluginConfig { PluginFolders = [root] };
            var loader = new PluginLoader(config, NullLogger);

            var result = loader.LoadPlugins();

            result.Plugins.Should().Contain(p => p.PluginId == "fixture-valid");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Test]
    public void LoadPlugins_Subdirectory_SkipsResourceDlls()
    {
        var root = Path.Combine(Path.GetTempPath(), $"plugin-subdir3-{Guid.NewGuid()}");
        var subDir = Path.Combine(root, "MyPlugin");
        Directory.CreateDirectory(subDir);

        try
        {
            var srcDll = Path.Combine(FixtureAssemblyDir, "MqttProbe.PluginLoader.Fixtures.dll");
            File.Copy(srcDll, Path.Combine(subDir, "MyPlugin.dll"));
            File.WriteAllText(Path.Combine(subDir, "MyPlugin.resources.dll"), "fake");

            var config = new PluginConfig { PluginFolders = [root] };
            var loader = new PluginLoader(config, NullLogger);

            var result = loader.LoadPlugins();

            result.Plugins.Should().Contain(p => p.PluginId == "fixture-valid");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }
}
