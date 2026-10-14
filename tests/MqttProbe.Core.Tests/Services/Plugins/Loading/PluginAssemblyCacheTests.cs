using Microsoft.Extensions.Logging.Abstractions;
using MqttProbe.Core.Models.Plugins;
using MqttProbe.Core.Services.Platform;
using MqttProbe.Core.Services.Plugins;
using MqttProbe.Core.Services.Plugins.Loading;
using MqttProbe.Core.Services.Plugins.Packaging;

namespace MqttProbe.Core.Tests.Services.Plugins.Loading;

[TestFixture]
public class PluginAssemblyCacheTests
{
    private static readonly string _missingFolder =
        Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid().ToString("N"));

    private string _folderWithAssembly = null!;

    [SetUp]
    public void SetUp()
    {
        _folderWithAssembly = Path.Combine(Path.GetTempPath(), "asm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folderWithAssembly);

        // A real loadable DLL, not an empty directory, so the guard is actually proven.
        var source = typeof(PluginAssemblyCache).Assembly.Location;
        File.Copy(source, Path.Combine(_folderWithAssembly, "Contoso.Decoder.dll"));
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            if (Directory.Exists(_folderWithAssembly))
            {
                Directory.Delete(_folderWithAssembly, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The supported-path test loads the copy for real, so Windows can hold it briefly.
        }
    }

    [Test]
    public void GetOrLoad_Unsupported_SuppliedFolderIsNotScanned()
    {
        var config = new PluginConfig { PluginFolders = [_folderWithAssembly] };

        var result = new PluginAssemblyCache(false).GetOrLoad(config, NullLoggerFactory.Instance);

        result.Plugins.Should().BeEmpty();
        result.LoadedPaths.Should().BeEmpty();
        result.Diagnostics.Should().ContainSingle(d =>
            d.Source == "loader" &&
            d.Severity == DiagnosticSeverity.Info &&
            d.Message.Contains("not supported on this platform"));
        result.Diagnostics[0].Message.Should().NotContain("only built-in plugins are loaded",
            "imported protobuf schemas register too, so the message must not claim they are absent");
        result.Diagnostics[0].Message.Should().NotContain("schema",
            "the guard is about assemblies only and should not speak for schema packages");
        result.Diagnostics.Should().NotContain(d => d.SourcePath != null);
    }

    [Test]
    public void GetOrLoad_Unsupported_MissingFolderIsNotReported()
    {
        var config = new PluginConfig { PluginFolders = [_missingFolder] };

        var result = new PluginAssemblyCache(false).GetOrLoad(config, NullLoggerFactory.Instance);

        result.Diagnostics.Should().ContainSingle();
        result.Diagnostics[0].Message.Should().NotContain(_missingFolder);
    }

    [Test]
    public void GetOrLoad_Supported_ScansConfiguredFolder()
    {
        var config = new PluginConfig { PluginFolders = [_folderWithAssembly] };

        var result = new PluginAssemblyCache(true).GetOrLoad(config, NullLoggerFactory.Instance);

        result.Diagnostics.Should().NotContain(d => d.Message.Contains("not supported on this platform"));
        result.Diagnostics.Should().Contain(d => d.SourcePath == Path.Combine(_folderWithAssembly, "Contoso.Decoder.dll"));
    }

    [Test]
    public void GetOrLoad_Unsupported_IsCachedAcrossCalls()
    {
        var config = new PluginConfig { PluginFolders = [_folderWithAssembly] };
        var cache = new PluginAssemblyCache(false);

        var first = cache.GetOrLoad(config, NullLoggerFactory.Instance);
        var second = cache.GetOrLoad(config, NullLoggerFactory.Instance);

        second.Should().BeSameAs(first);
    }

    [Test]
    public void BuildPluginRegistry_UnsupportedAssemblyLoading_KeepsBuiltInDecoders()
    {
        var config = new PluginConfig { PluginFolders = [_folderWithAssembly] };

        var registry = MqttProbePluginStartup.BuildPluginRegistry(
            config, NullLoggerFactory.Instance, new PluginAssemblyCache(false));

        registry.FindDecoder("json").Should().NotBeNull();
        registry.FindDecoder("sparkplug-b").Should().NotBeNull();
        registry.Decoders.Should().HaveCount(9);
        registry.LoadedPackagePaths.Should().BeEmpty();
    }

    [Test]
    public void DefaultGuard_AllowsLoading_OnEveryNonIosHost()
    {
        if (OperatingSystem.IsIOS())
        {
            Assert.Ignore("The default guard is expected to block loading on iOS.");
        }

        var config = new PluginConfig { PluginFolders = [_folderWithAssembly] };

        var result = new PluginAssemblyCache().GetOrLoad(config, NullLoggerFactory.Instance);

        // On an Android lane this fails if the default widens back to the inspector.
        result.Diagnostics.Should().NotContain(d => d.Message.Contains("not supported on this platform"));
        result.Diagnostics.Should().Contain(d => d.SourcePath == Path.Combine(_folderWithAssembly, "Contoso.Decoder.dll"));
    }

    [Test]
    public void BinaryPackageValidation_StillExcludesAndroidAndIos()
    {
        // Install-time validation stays wider than the loader guard: both mobile platforms.
        PluginAssemblyInspector.AssemblyPluginsSupported
            .Should().Be(!OperatingSystem.IsAndroid() && !OperatingSystem.IsIOS());
    }

    [Test]
    public void RuntimeCapability_IsTheIosOnlyLoaderGuard_NotTheWiderInspector()
    {
        if (OperatingSystem.IsIOS())
        {
            Assert.Ignore("The runtime capability is expected to be false on iOS.");
        }

        // SupportsAssemblyPlugins on the Maui host reports what the loader can run, which
        // excludes iOS only. Widening it to the inspector would misreport Android, where
        // bundled or configured DLLs still load even though binary archives cannot be
        // imported. On an Android lane the two must therefore disagree.
        PluginAssemblyInspector.AssemblyPluginsSupported
            .Should().Be(!OperatingSystem.IsAndroid(),
                "the inspector stays the wider gate: it refuses binary archives on Android too");
    }
}

[TestFixture]
public class IAppInfoServicePluginCapabilityTests
{
    // Stands in for desktop and web, which rely on the interface default.
    private sealed class HostAppInfoService : IAppInfoService
    {
        public string GetVersion() => "1.2.3";
        public bool RequiresAuthentication => false;
        public bool IsNative => true;
        public bool IsOidcMode => false;
        public string? ProviderDisplayName => null;
    }

    [Test]
    public void SupportsPluginInstallation_DefaultsToTrue_WhenHostDoesNotOptOut()
    {
        IAppInfoService appInfo = new HostAppInfoService();

        appInfo.SupportsPluginInstallation.Should().BeTrue();
    }

    [Test]
    public void SupportsAssemblyPlugins_DefaultsToTrue_WhenHostDoesNotOptOut()
    {
        IAppInfoService appInfo = new HostAppInfoService();

        appInfo.SupportsAssemblyPlugins.Should().BeTrue();
    }

    [Test]
    public void Schema_Only_Host_Keeps_Package_Import_While_Refusing_Assemblies()
    {
        IAppInfoService appInfo = new SchemaOnlyHostAppInfoService();

        appInfo.SupportsPluginInstallation.Should().BeTrue(
            "a schema package is data, so the import affordance stays available");
        appInfo.SupportsAssemblyPlugins.Should().BeFalse();
    }

    private sealed class SchemaOnlyHostAppInfoService : IAppInfoService
    {
        public string GetVersion() => "1.2.3";
        public bool RequiresAuthentication => false;
        public bool IsNative => true;
        public bool IsOidcMode => false;
        public string? ProviderDisplayName => null;
        public bool SupportsAssemblyPlugins => false;
    }
}
