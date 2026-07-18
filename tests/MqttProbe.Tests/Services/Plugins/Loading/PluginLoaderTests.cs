using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MqttProbe.Models.Plugins;
using MqttProbe.Services.Plugins;
using MqttProbe.Services.Plugins.Loading;

namespace MqttProbe.Shared.Tests.Services.Plugins.Loading;

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
            NUnit.Framework.Assert.Ignore(
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
        var config = new PluginConfig
        {
            PluginFolders = [TestOutputDir]
        };
        var loader = new PluginLoader(config, NullLogger);

        var result = loader.LoadPlugins();

        result.Diagnostics.Should().Contain(d =>
            d.Source == "loader" &&
            d.Severity == DiagnosticSeverity.Info &&
            d.Message.Contains("No IMqttProbePlugin"));

        result.Plugins.Should().BeEmpty();
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
    public void PluginLoadContext_SharedAssemblyNames_ContainsMqttProbeShared()
    {
        PluginLoadContext.SharedAssemblyNames.Should().Contain("MqttProbe.Shared");
    }

    [Test]
    public void PluginLoadContext_IsSharedAssembly_ReturnsTrueForSharedName()
    {
        PluginLoadContext.IsSharedAssembly("MqttProbe.Shared").Should().BeTrue();
    }

    [Test]
    public void PluginLoadContext_IsSharedAssembly_ReturnsFalseForNonSharedName()
    {
        PluginLoadContext.IsSharedAssembly("SomeThirdPartyLib").Should().BeFalse();
        PluginLoadContext.IsSharedAssembly(null).Should().BeFalse();
        PluginLoadContext.IsSharedAssembly(string.Empty).Should().BeFalse();
    }
}
