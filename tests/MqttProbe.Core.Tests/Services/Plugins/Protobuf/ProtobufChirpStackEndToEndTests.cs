using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MqttProbe.Core.Models.Plugins;
using MqttProbe.Core.Services.Platform;
using MqttProbe.Core.Services.Plugins;
using MqttProbe.Core.Services.Plugins.Loading;
using MqttProbe.Core.Services.Plugins.Packaging;
using MqttProbe.Core.Services.Plugins.Protobuf;
using MqttProbe.PluginContracts;
using static MqttProbe.Core.Tests.Services.Plugins.Packaging.PluginPackageTestData;
using static MqttProbe.Core.Tests.Services.Plugins.Protobuf.ChirpStackSampleFixture;

namespace MqttProbe.Core.Tests.Services.Plugins.Protobuf;

[TestFixture]
public class ProtobufChirpStackEndToEndTests
{
    private static string StageAsDroppedInPluginFolder()
    {
        var pluginFolder = Path.Combine(Path.GetTempPath(), "mqttprobe-plugins-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(SamplesProtobufDir(), Path.Combine(pluginFolder, ProtobufSchemaFolderLoader.FolderName));
        return pluginFolder;
    }

    private static byte[] SamplePayload()
    {
        var b64Path = Path.Combine(ChirpStackSampleDir(), "sample-uplink.b64");
        File.Exists(b64Path).Should().BeTrue($"sample fixture must be committed at {b64Path}");
        return Convert.FromBase64String(File.ReadAllText(b64Path).Trim());
    }

    [Test]
    public void Shipped_Sample_Folder_Decodes_Uplink_When_Dropped_In()
    {
        var pluginFolder = StageAsDroppedInPluginFolder();

        var sources = ProtobufSchemaFolderLoader.Discover([pluginFolder]);
        sources.Should().ContainSingle("the shipped manifest must be discovered as a drop-in");
        Path.GetFileName(sources[0].SchemaRoot).Should().Be("chirpstack",
            "the sample installs as its own schema-set subdirectory");

        var registry = new ProtobufSchemaRegistry(sources);
        ChirpStackSampleDiagnostics.ShouldMatchKnownWarnings(registry);
        registry.HasAnySchemas.Should().BeTrue();

        var envelope = new ProtobufPayloadDecoder(registry)
            .Decode(MakeArgs("application/1/device/0102030405060708/event/up", SamplePayload()));

        envelope.IsFailure.Should().BeFalse();
        using var doc = JsonDocument.Parse(envelope.DisplayText);
        doc.RootElement.GetProperty("deduplication_id").GetString().Should().Be("dedup-1");
        doc.RootElement.GetProperty("f_cnt").GetInt64().Should().Be(42);
        doc.RootElement.GetProperty("f_port").GetInt64().Should().Be(10);
        doc.RootElement.GetProperty("device_info").GetProperty("dev_eui").GetString().Should().Be("0102030405060708");
        doc.RootElement.GetProperty("device_info").GetProperty("device_name").GetString().Should().Be("sensor-a");
    }

    [TestCase("event/up", "UplinkEvent")]
    [TestCase("event/join", "JoinEvent")]
    [TestCase("event/ack", "AckEvent")]
    [TestCase("event/txack", "TxAckEvent")]
    [TestCase("event/log", "LogEvent")]
    [TestCase("event/status", "StatusEvent")]
    [TestCase("event/location", "LocationEvent")]
    [TestCase("command/down", "DownlinkCommand")]
    public void Every_ChirpStack_Topic_Routes_To_Its_Message(string suffix, string expectedMessage)
    {
        var sources = ProtobufSchemaFolderLoader.Discover([StageAsDroppedInPluginFolder()]);
        var registry = new ProtobufSchemaRegistry(sources);
        ChirpStackSampleDiagnostics.ShouldMatchKnownWarnings(registry);

        registry.TryResolveByTopic($"application/1/device/0102030405060708/{suffix}", out var message)
            .Should().BeTrue($"the sample manifest must map {suffix}");
        message.Name.Should().Be(expectedMessage);
    }

    [Test]
    public void Dropped_In_Sample_Registers_Protobuf_Decoder_At_Startup()
    {
        var config = new PluginConfig { PluginFolders = { StageAsDroppedInPluginFolder() } };

        var registry = MqttProbePluginStartup.BuildPluginRegistry(
            config, NullLoggerFactory.Instance);

        registry.FindDecoder("protobuf").Should().NotBeNull();
        registry.FindDetector(MakeArgs("application/1/device/0102030405060708/event/up", SamplePayload()))
            !.FormatId.Should().Be("protobuf");
    }

    [Test]
    public async Task Sample_Installs_As_A_Package_And_Decodes_An_Uplink()
    {
        var pluginFolder = Path.Combine(Path.GetTempPath(), "mqttprobe-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(pluginFolder);

        var appInfo = Substitute.For<IAppInfoService>();
        appInfo.GetVersion().Returns("1.0.3");

        var config = new PluginConfig();
        config.PluginFolders.Add(pluginFolder);

        var installer = new PluginPackageInstaller(
            config, appInfo, new PluginInstallSession(), new PluginArchiveLimits(), NullLoggerFactory.Instance);

        var outcome = await installer.InstallAsync(PackSampleAsPackage(), CancellationToken.None);

        outcome.Succeeded.Should().BeTrue(outcome.Error);
        outcome.Manifest!.Id.Should().Be("chirpstack");
        outcome.RequiresRestart.Should().BeFalse();

        var sources = ProtobufSchemaFolderLoader.Discover([pluginFolder]);
        sources.Should().ContainSingle("the installed package must be discovered by the existing loader");

        var registry = new ProtobufSchemaRegistry(sources);
        registry.HasAnySchemas.Should().BeTrue();

        var envelope = new ProtobufPayloadDecoder(registry)
            .Decode(MakeArgs("application/1/device/0102030405060708/event/up", SamplePayload()));

        envelope.IsFailure.Should().BeFalse(envelope.FailureReason);

        Directory.Delete(pluginFolder, recursive: true);
    }

    private static PluginConfig SchemaOnlyHostConfig(string pluginFolder, IAppInfoService appInfo)
    {
        appInfo.SupportsPluginInstallation.Returns(true);
        appInfo.SupportsAssemblyPlugins.Returns(false);

        var config = new PluginConfig { AllowBinaryPackages = false };
        config.PluginFolders.Add(pluginFolder);
        return config;
    }

    [Test]
    public async Task Schema_Only_Host_Installs_The_Sample_And_Registers_Its_Decoder()
    {
        var pluginFolder = Path.Combine(Path.GetTempPath(), "mqttprobe-schemaonly-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(pluginFolder);

        var appInfo = Substitute.For<IAppInfoService>();
        appInfo.GetVersion().Returns("1.0.3");
        var config = SchemaOnlyHostConfig(pluginFolder, appInfo);

        var installer = new PluginPackageInstaller(
            config, appInfo, new PluginInstallSession(), new PluginArchiveLimits(), NullLoggerFactory.Instance);

        var outcome = await installer.InstallAsync(PackSampleAsPackage(), CancellationToken.None);
        outcome.Succeeded.Should().BeTrue(outcome.Error);

        var registry = MqttProbePluginStartup.BuildPluginRegistry(
            config, NullLoggerFactory.Instance, new PluginAssemblyCache(false));

        registry.FindDecoder("protobuf").Should().NotBeNull(
            "schema packages are data, so the assembly guard must not block them");
        registry.Diagnostics.Should().Contain(d => d.Message.Contains("not supported on this platform"));

        var envelope = registry.FindDecoder("protobuf")!
            .Decode(MakeArgs("application/1/device/0102030405060708/event/up", SamplePayload()));

        envelope.IsFailure.Should().BeFalse(envelope.FailureReason);

        Directory.Delete(pluginFolder, recursive: true);
    }

    [Test]
    public async Task Schema_Only_Host_Still_Rejects_An_Assembly_Package()
    {
        var pluginFolder = Path.Combine(Path.GetTempPath(), "mqttprobe-schemaonly-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(pluginFolder);

        var appInfo = Substitute.For<IAppInfoService>();
        appInfo.GetVersion().Returns("1.0.3");
        var config = SchemaOnlyHostConfig(pluginFolder, appInfo);

        var installer = new PluginPackageInstaller(
            config, appInfo, new PluginInstallSession(), new PluginArchiveLimits(), NullLoggerFactory.Instance);

        var package = AssemblyPackage(
            "demoplugin", typeof(IMqttProbePlugin).Assembly.Location);

        var outcome = await installer.InstallAsync(package, CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        outcome.Error.Should().Contain("AllowBinaryPackages");
        Directory.Delete(pluginFolder, recursive: true);
    }
}
