using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MQTTnet;
using MqttProbe.Models.Plugins;
using MqttProbe.Services.Platform;
using MqttProbe.Services.Plugins;
using MqttProbe.Services.Plugins.Packaging;
using MqttProbe.Services.Plugins.Protobuf;
using NSubstitute;

namespace MqttProbe.Tests.Services.Plugins.Protobuf;

[TestFixture]
public class ProtobufChirpStackEndToEndTests
{
    private static string SamplesProtobufDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "samples", "protobuf")))
            dir = dir.Parent;
        dir.Should().NotBeNull("repo root with samples/protobuf must be found");
        return Path.Combine(dir!.FullName, "samples", "protobuf");
    }

    private static string ChirpStackSampleDir() =>
        Path.Combine(SamplesProtobufDir(), "chirpstack");

    private static string StageAsDroppedInPluginFolder()
    {
        var pluginFolder = Path.Combine(Path.GetTempPath(), "mqttprobe-plugins-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(SamplesProtobufDir(), Path.Combine(pluginFolder, ProtobufSchemaFolderLoader.FolderName));
        return pluginFolder;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        foreach (var sub in Directory.GetDirectories(source))
            CopyDirectory(sub, Path.Combine(destination, Path.GetFileName(sub)));
    }

    private static MqttApplicationMessageReceivedEventArgs MakeArgs(string topic, byte[] payload)
    {
        var appMsg = new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload).Build();
        var packet = new MQTTnet.Packets.MqttPublishPacket { Topic = topic };
        return new MqttApplicationMessageReceivedEventArgs("test-client", appMsg, packet, null);
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
        registry.Diagnostics.Should().BeEmpty(
            "the unmodified ChirpStack schema must parse with no errors or warnings");
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
        registry.Diagnostics.Should().BeEmpty();

        registry.TryResolveByTopic($"application/1/device/0102030405060708/{suffix}", out var message)
            .Should().BeTrue($"the sample manifest must map {suffix}");
        message.Name.Should().Be(expectedMessage);
    }

    [Test]
    public void Dropped_In_Sample_Registers_Protobuf_Decoder_At_Startup()
    {
        var config = new PluginConfig { PluginFolders = { StageAsDroppedInPluginFolder() } };

        var registry = MqttProbePluginStartup.BuildPluginRegistry(
            config, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

        registry.FindDecoder("protobuf").Should().NotBeNull();
        registry.FindDetector(MakeArgs("application/1/device/0102030405060708/event/up", SamplePayload()))
            !.FormatId.Should().Be("protobuf");
    }

    private static MemoryStream PackSampleAsPackage()
    {
        var buffer = new MemoryStream();
        var sourceDir = ChirpStackSampleDir();

        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceDir, file).Replace('\\', '/');

                if (relative.EndsWith(".b64", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                zip.CreateEntryFromFile(file, relative);
            }
        }

        buffer.Position = 0;
        return buffer;
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
}
