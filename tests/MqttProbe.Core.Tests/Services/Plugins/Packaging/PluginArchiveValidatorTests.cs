using System.IO.Compression;
using System.Text;
using MqttProbe.Core.Models.Plugins;
using MqttProbe.Core.Services.Plugins.Packaging;

namespace MqttProbe.Core.Tests.Services.Plugins.Packaging;

[TestFixture]
public class PluginArchiveValidatorTests
{
    private static ZipArchive ArchiveWith(params (string Name, string Content)[] entries)
    {
        var buffer = new MemoryStream();

        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8);
                writer.Write(content);
            }
        }

        buffer.Position = 0;
        return new ZipArchive(buffer, ZipArchiveMode.Read);
    }

    [Test]
    public void Accepts_A_Plain_Schema_Bundle()
    {
        using var archive = ArchiveWith(
            ("mqttprobe-plugin.json", "{}"),
            ("protobuf-schemas.json", "{}"),
            ("integration/integration.proto", "syntax = \"proto3\";"));

        PluginArchiveValidator
            .ValidateEntries(archive, PluginPackageKinds.ProtobufSchemas, new PluginArchiveLimits())
            .IsValid.Should().BeTrue();
    }

    [TestCase("../escape.proto")]
    [TestCase("nested/../../escape.proto")]
    [TestCase("/absolute.proto")]
    [TestCase("C:/windows/system32/evil.proto")]
    public void Rejects_Entries_That_Escape_The_Root(string entryName)
    {
        PluginArchiveValidator.IsSafeEntryPath(entryName).Should().BeFalse();
    }

    // Path.IsPathRooted only recognises a drive-letter prefix as rooted on Windows, so
    // these assert on the returned value directly rather than relying on host OS behaviour
    // (the ubuntu-latest CI runner would otherwise treat these as safe relative paths).
    [TestCase("C:/windows/system32/evil.proto")]
    [TestCase("C:evil.proto")]
    [TestCase("d:/data/evil.proto")]
    public void Rejects_Windows_Drive_Letter_Paths_Regardless_Of_Host_Os(string entryName)
    {
        PluginArchiveValidator.IsSafeEntryPath(entryName).Should().BeFalse();
    }

    [TestCase("integration/integration.proto")]
    [TestCase("mqttprobe-plugin.json")]
    [TestCase("docs/readme.md")]
    public void Accepts_Ordinary_Relative_Entries(string entryName)
    {
        PluginArchiveValidator.IsSafeEntryPath(entryName).Should().BeTrue();
    }

    [Test]
    public void TryResolveDestination_Rejects_A_Path_Outside_The_Root()
    {
        var root = Path.Combine(Path.GetTempPath(), "mqttprobe-root-" + Guid.NewGuid().ToString("N"));

        PluginArchiveValidator.TryResolveDestination(root, "../outside.proto", out _)
            .Should().BeFalse();
    }

    [Test]
    public void TryResolveDestination_Accepts_A_Nested_Path()
    {
        var root = Path.Combine(Path.GetTempPath(), "mqttprobe-root-" + Guid.NewGuid().ToString("N"));

        PluginArchiveValidator.TryResolveDestination(root, "a/b/c.proto", out var destination)
            .Should().BeTrue();
        destination.Should().StartWith(Path.GetFullPath(root));
    }

    [Test]
    public void Rejects_A_Dll_In_A_Schema_Bundle()
    {
        using var archive = ArchiveWith(("evil.dll", "MZ"));

        var result = PluginArchiveValidator.ValidateEntries(
            archive, PluginPackageKinds.ProtobufSchemas, new PluginArchiveLimits());

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain(".dll");
    }

    [Test]
    public void Accepts_A_Dll_In_An_Assembly_Package()
    {
        using var archive = ArchiveWith(("demo.dll", "MZ"), ("mqttprobe-plugin.json", "{}"));

        PluginArchiveValidator
            .ValidateEntries(archive, PluginPackageKinds.Assembly, new PluginArchiveLimits())
            .IsValid.Should().BeTrue();
    }

    [Test]
    public void Rejects_An_Archive_With_Too_Many_Entries()
    {
        var entries = Enumerable.Range(0, 12)
            .Select(i => ($"file{i}.proto", "x"))
            .ToArray();

        using var archive = ArchiveWith(entries);

        var result = PluginArchiveValidator.ValidateEntries(
            archive,
            PluginPackageKinds.ProtobufSchemas,
            new PluginArchiveLimits { MaxEntries = 10 });

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("entries");
    }

    [Test]
    public void Rejects_An_Archive_That_Expands_Beyond_The_Size_Ceiling()
    {
        using var archive = ArchiveWith(("big.proto", new string('a', 5_000)));

        var result = PluginArchiveValidator.ValidateEntries(
            archive,
            PluginPackageKinds.ProtobufSchemas,
            new PluginArchiveLimits { MaxUncompressedBytes = 1_000 });

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("uncompressed");
    }

    [Test]
    public void Rejects_A_Directory_Named_Entry_That_Actually_Contains_Data()
    {
        using var archive = ArchiveWith(("evil.dll/", new string('a', 10_000)));

        var result = PluginArchiveValidator.ValidateEntries(
            archive, PluginPackageKinds.ProtobufSchemas, new PluginArchiveLimits());

        result.IsValid.Should().BeFalse();
    }

    [Test]
    public void Rejects_A_Highly_Compressed_Entry()
    {
        using var archive = ArchiveWith(("bomb.proto", new string('a', 2_000_000)));

        var result = PluginArchiveValidator.ValidateEntries(
            archive,
            PluginPackageKinds.ProtobufSchemas,
            new PluginArchiveLimits { MaxCompressionRatio = 10 });

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("compression");
    }
}
