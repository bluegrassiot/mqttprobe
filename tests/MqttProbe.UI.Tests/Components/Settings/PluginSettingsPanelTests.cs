using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MqttProbe.Core.Models.Plugins;
using MqttProbe.Core.Services.Platform;
using MqttProbe.Core.Services.Plugins;
using MqttProbe.Core.Services.Plugins.Loading;
using MqttProbe.Core.Services.Plugins.Packaging;
using MqttProbe.Core.Services.Plugins.Pipeline;
using MqttProbe.Core.Services.Plugins.Protobuf;
using MqttProbe.Core.Services.Plugins.Registry;
using MqttProbe.UI.Components.Plugins;
using MqttProbe.UI.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.UI.Tests.Components.Plugins;

[TestFixture]
public class PluginSettingsPanelTests : BunitTestContext
{
    private string _pluginFolder = string.Empty;
    private PluginInstallSession _session = null!;
    private IPluginPackagePicker _picker = null!;
    private IPluginInputCapability _inputCapability = null!;
    private IDialogService _dialogService = null!;

    [SetUp]
    public void SetUp()
    {
        _pluginFolder = Path.Combine(Path.GetTempPath(), "mqttprobe-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_pluginFolder);

        _session = new PluginInstallSession();
        _picker = Substitute.For<IPluginPackagePicker>();
        _inputCapability = Substitute.For<IPluginInputCapability>();
        _dialogService = Substitute.For<IDialogService>();

        Services.AddSingleton(_picker);
        Services.AddSingleton(_inputCapability);
        Services.AddSingleton(_dialogService);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_pluginFolder))
        {
            Directory.Delete(_pluginFolder, recursive: true);
        }
    }

    private void InstallSchemaManifest(string id, string name, string version)
    {
        var root = Path.Combine(_pluginFolder, ProtobufSchemaFolderLoader.FolderName, id);
        Directory.CreateDirectory(root);

        File.WriteAllText(Path.Combine(root, PluginManifestValidator.FileName),
            $$"""
            { "id": "{{id}}", "name": "{{name}}", "version": "{{version}}", "kind": "protobuf-schemas" }
            """);
    }

    private void InstallAssemblyManifest(string id, string name, string version)
    {
        var root = Path.Combine(_pluginFolder, id);
        Directory.CreateDirectory(root);

        File.WriteAllText(Path.Combine(root, PluginManifestValidator.FileName),
            $$"""
            { "id": "{{id}}", "name": "{{name}}", "version": "{{version}}", "kind": "assembly" }
            """);
    }

    private void RegisterPluginServices(PluginConfig config)
    {
        var registry = new PluginRegistryBuilder().Build([], []);
        var pipeline = new PayloadPipeline(registry, NullLogger<PayloadPipeline>.Instance);
        var appInfo = Substitute.For<IAppInfoService>();
        appInfo.GetVersion().Returns("1.0.3");

        Services.AddSingleton(new PluginInventoryService(config, pipeline, _session));
        Services.AddSingleton(new PluginPackageInstaller(
            config, appInfo, _session, new PluginArchiveLimits(), NullLoggerFactory.Instance));
        Services.AddSingleton(new PluginReloadService(
            config, pipeline, _session, new PluginAssemblyCache(), NullLoggerFactory.Instance));
        Services.AddSingleton(_session);
    }

    private PluginConfig ConfigForFolder()
    {
        var config = new PluginConfig();
        config.PluginFolders.Add(_pluginFolder);
        return config;
    }

    [Test]
    public void Renders_A_Row_Per_Installed_Plugin()
    {
        InstallSchemaManifest("demo", "Demo Schemas", "1.0.0");
        InstallAssemblyManifest("otherplugin", "Other Plugin", "2.1.0");
        RegisterPluginServices(ConfigForFolder());

        EnsureMudProviders();

        var cut = Render<PluginSettingsPanel>();

        cut.Markup.Should().Contain("Demo Schemas");
        cut.Markup.Should().Contain("Other Plugin");
        cut.Markup.Should().Contain("1.0.0");
        cut.Markup.Should().Contain("2.1.0");
    }

    [Test]
    public void Shows_The_Apply_Button_When_Only_Schema_Changes_Are_Pending()
    {
        InstallSchemaManifest("demo", "Demo Schemas", "1.0.0");
        _session.Record("demo", requiresRestart: false);
        RegisterPluginServices(ConfigForFolder());

        EnsureMudProviders();

        var cut = Render<PluginSettingsPanel>();

        cut.FindAll("[data-testid=apply-plugins-button]").Should().ContainSingle();
    }

    [Test]
    public void Shows_A_Restart_Message_When_An_Assembly_Change_Is_Pending()
    {
        InstallAssemblyManifest("demoplugin", "Demo Plugin", "1.0.0");
        _session.Record("demoplugin", requiresRestart: true);
        RegisterPluginServices(ConfigForFolder());

        EnsureMudProviders();

        var cut = Render<PluginSettingsPanel>();

        cut.FindAll("[data-testid=apply-plugins-button]").Should().BeEmpty();
        cut.Markup.Should().Contain("Restart");
    }

    [Test]
    public void Shows_Both_The_Apply_Button_And_The_Restart_Message_When_Both_Are_Pending()
    {
        InstallSchemaManifest("demo", "Demo Schemas", "1.0.0");
        InstallAssemblyManifest("demoplugin", "Demo Plugin", "1.0.0");
        _session.Record("demo", requiresRestart: false);
        _session.Record("demoplugin", requiresRestart: true);
        RegisterPluginServices(ConfigForFolder());

        EnsureMudProviders();

        var cut = Render<PluginSettingsPanel>();

        cut.FindAll("[data-testid=apply-plugins-button]").Should().ContainSingle(
            "the schema change can still activate without a restart even while the assembly change awaits one");
        cut.Markup.Should().Contain("Restart");
    }

    [Test]
    public void Hides_Install_When_No_Plugin_Folder_Is_Writable()
    {
        RegisterPluginServices(new PluginConfig());

        EnsureMudProviders();

        var cut = Render<PluginSettingsPanel>();

        cut.FindAll("[data-testid=install-plugin-button]").Should().BeEmpty();
    }

    [Test]
    public void Shows_The_Failure_Detail_For_A_Failed_Plugin()
    {
        InstallSchemaManifest("demo", "Demo Schemas", "1.0.0");

        var config = ConfigForFolder();
        var protobufFolder = Path.Combine(_pluginFolder, ProtobufSchemaFolderLoader.FolderName, "demo");
        var builder = new PluginRegistryBuilder();
        builder.AddDiagnostic(new PluginDiagnosticEntry
        {
            Source = "protobuf",
            SourcePath = protobufFolder,
            Severity = DiagnosticSeverity.Error,
            Message = "Declared message type not found"
        });
        var registry = builder.Build([], []);

        var pipeline = new PayloadPipeline(registry, NullLogger<PayloadPipeline>.Instance);
        var appInfo = Substitute.For<IAppInfoService>();
        appInfo.GetVersion().Returns("1.0.3");

        Services.AddSingleton(new PluginInventoryService(config, pipeline, _session));
        Services.AddSingleton(new PluginPackageInstaller(
            config, appInfo, _session, new PluginArchiveLimits(), NullLoggerFactory.Instance));
        Services.AddSingleton(new PluginReloadService(
            config, pipeline, _session, new PluginAssemblyCache(), NullLoggerFactory.Instance));
        Services.AddSingleton(_session);

        EnsureMudProviders();

        var cut = Render<PluginSettingsPanel>();

        cut.Markup.Should().Contain("Declared message type not found");
    }

    [Test]
    public void Panel_RendersNoPanelChrome()
    {
        RegisterPluginServices(ConfigForFolder());
        EnsureMudProviders();

        var cut = Render<PluginSettingsPanel>();

        cut.FindAll(".app-chrome-panel").Should().BeEmpty();
        cut.Markup.Should().NotContain("mud-typography-h6");
    }
}
