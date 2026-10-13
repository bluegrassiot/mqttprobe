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
    private IAppInfoService _appInfo = null!;

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

    private void RegisterPluginServices(PluginConfig config, bool supportsAssemblyPlugins = true)
    {
        var registry = new PluginRegistryBuilder().Build([], []);
        var pipeline = new PayloadPipeline(registry, NullLogger<PayloadPipeline>.Instance);
        var appInfo = Substitute.For<IAppInfoService>();
        appInfo.GetVersion().Returns("1.0.3");
        // NSubstitute answers default(bool), not the interface default.
        appInfo.SupportsPluginInstallation.Returns(true);
        appInfo.SupportsAssemblyPlugins.Returns(supportsAssemblyPlugins);
        _appInfo = appInfo;

        Services.AddSingleton(new PluginInventoryService(config, pipeline, _session));
        Services.AddSingleton(new PluginPackageInstaller(
            config, appInfo, _session, new PluginArchiveLimits(), NullLoggerFactory.Instance));
        Services.AddSingleton(new PluginReloadService(
            config, pipeline, _session, new PluginAssemblyCache(), NullLoggerFactory.Instance));
        Services.AddSingleton(_session);
        Services.AddSingleton(appInfo);
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
        appInfo.SupportsPluginInstallation.Returns(true);
        appInfo.SupportsAssemblyPlugins.Returns(true);

        Services.AddSingleton(new PluginInventoryService(config, pipeline, _session));
        Services.AddSingleton(new PluginPackageInstaller(
            config, appInfo, _session, new PluginArchiveLimits(), NullLoggerFactory.Instance));
        Services.AddSingleton(new PluginReloadService(
            config, pipeline, _session, new PluginAssemblyCache(), NullLoggerFactory.Instance));
        Services.AddSingleton(_session);
        Services.AddSingleton(appInfo);

        EnsureMudProviders();

        var cut = Render<PluginSettingsPanel>();

        cut.Markup.Should().Contain("Declared message type not found");
    }

    [Test]
    public void Renders_Nothing_When_The_Build_Cannot_Install_Plugins()
    {
        InstallSchemaManifest("demo", "Demo Schemas", "1.0.0");
        RegisterPluginServices(ConfigForFolder());
        _appInfo.SupportsPluginInstallation.Returns(false);

        EnsureMudProviders();

        var cut = Render<PluginSettingsPanel>();

        cut.Markup.Should().BeEmpty();
    }

    [Test]
    public void Shows_No_Restriction_Message_When_The_Build_Cannot_Install_Plugins()
    {
        RegisterPluginServices(ConfigForFolder());
        _appInfo.SupportsPluginInstallation.Returns(false);

        EnsureMudProviders();

        var cut = Render<PluginSettingsPanel>();

        cut.Markup.Should().NotContain("App Store");
        cut.Markup.Should().NotContain("cannot install plugins");
        cut.Markup.Should().NotContain("No plugins installed");
        cut.Markup.Should().NotContain("No writable plugin folder");
        cut.FindAll("table").Should().BeEmpty();
    }

    [Test]
    public void Renders_The_Usual_Panel_When_The_Build_Can_Install_Plugins()
    {
        InstallSchemaManifest("demo", "Demo Schemas", "1.0.0");
        RegisterPluginServices(ConfigForFolder());
        _appInfo.SupportsPluginInstallation.Returns(true);

        EnsureMudProviders();

        var cut = Render<PluginSettingsPanel>();

        cut.Markup.Should().Contain("Demo Schemas");
        cut.FindAll("[data-testid=install-plugin-button]").Should().ContainSingle();
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

    [Test]
    public void Import_Control_Stays_Enabled_When_Only_Schemas_Are_Supported()
    {
        RegisterPluginServices(ConfigForFolder(), supportsAssemblyPlugins: false);

        EnsureMudProviders();

        var cut = Render<PluginSettingsPanel>();

        var button = cut.FindAll("[data-testid=install-plugin-button]").Should().ContainSingle().Which;
        button.TextContent.Should().Contain("Import schemas");
        button.HasAttribute("disabled").Should().BeFalse(
            "schemas are how a code-free host imports, so the control must stay usable");
    }

    [Test]
    public void Schema_Only_Host_Explains_The_Package_Without_Naming_Store_Rules()
    {
        RegisterPluginServices(ConfigForFolder(), supportsAssemblyPlugins: false);

        EnsureMudProviders();

        var cut = Render<PluginSettingsPanel>();

        cut.Markup.Should().Contain("protobuf .proto definitions");
        cut.Markup.Should().Contain("No schemas imported.");
        cut.Markup.Should().NotContain("App Store");
        cut.Markup.Should().NotContain("Install plugin");
    }

    [Test]
    public void Keeps_Plugin_Wording_When_Assemblies_Are_Supported()
    {
        InstallSchemaManifest("demo", "Demo Schemas", "1.0.0");
        RegisterPluginServices(ConfigForFolder());

        EnsureMudProviders();

        var cut = Render<PluginSettingsPanel>();

        var button = cut.FindAll("[data-testid=install-plugin-button]").Should().ContainSingle().Which;
        button.TextContent.Should().Contain("Install plugin");
        cut.Markup.Should().NotContain("Import schemas");
        cut.Markup.Should().NotContain("No schemas imported.");
    }

    [Test]
    public void Marks_A_Leftover_Assembly_Entry_As_Not_Runnable_And_Keeps_It_Removable()
    {
        InstallSchemaManifest("demo", "Demo Schemas", "1.0.0");
        InstallAssemblyManifest("demoplugin", "Demo Plugin", "1.0.0");
        RegisterPluginServices(ConfigForFolder(), supportsAssemblyPlugins: false);

        EnsureMudProviders();

        var cut = Render<PluginSettingsPanel>();

        cut.Markup.Should().Contain("Code plugins cannot run on this platform.");
        cut.FindAll("tbody button").Should().HaveCount(2);
    }

    [Test]
    public void Still_Applies_A_Pending_Schema_Change_On_A_Schema_Only_Host()
    {
        InstallSchemaManifest("demo", "Demo Schemas", "1.0.0");
        _session.Record("demo", requiresRestart: false);
        RegisterPluginServices(ConfigForFolder(), supportsAssemblyPlugins: false);

        EnsureMudProviders();

        var cut = Render<PluginSettingsPanel>();

        cut.FindAll("[data-testid=apply-plugins-button]").Should().ContainSingle();
    }

    [Test]
    public void Schema_Only_Host_Words_The_Missing_Folder_Alert_For_Schemas()
    {
        RegisterPluginServices(new PluginConfig(), supportsAssemblyPlugins: false);

        EnsureMudProviders();

        var cut = Render<PluginSettingsPanel>();

        cut.Markup.Should().Contain("No writable plugin folder is configured on this host, so schemas cannot be imported here.");
        cut.FindAll("[data-testid=install-plugin-button]").Should().BeEmpty();
    }
}
