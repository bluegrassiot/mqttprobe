using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using MqttProbe.Models.Plugins;
using MqttProbe.Services.Plugins.Packaging;
using MqttProbe.Services.Plugins.Protobuf;

namespace MqttProbe.Shared.Tests.Services.Plugins.Packaging;

internal static class PluginPackageTestData
{
    public const string DemoProto = """
        syntax = "proto3";
        package demo;
        message Reading { int32 id = 1; string label = 2; }
        """;

    private static string PluginManifestJson(
        string id,
        string version,
        string kind = "protobuf-schemas",
        string name = "Demo Schemas") =>
        $$"""
        {
          "id": "{{id}}",
          "name": "{{name}}",
          "version": "{{version}}",
          "kind": "{{kind}}"
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

    public static MemoryStream SchemaPackage(
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

            var proto = paddingBytes > 0
                ? DemoProto + "\n// " + Convert.ToBase64String(RandomNumberGenerator.GetBytes(paddingBytes))
                : DemoProto;

            Write(zip, "demo.proto", proto);
        }

        buffer.Position = 0;
        return buffer;
    }

    public static void WriteSchemaFilesToDisk(string root, string id = "demo", string version = "1.0.0")
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, PluginManifestValidator.FileName), PluginManifestJson(id, version));
        File.WriteAllText(
            Path.Combine(root, ProtobufSchemaFolderLoader.ManifestFileName), SchemaManifestJson("demo.Reading"));
        File.WriteAllText(Path.Combine(root, "demo.proto"), DemoProto);
    }

    public static MemoryStream SchemaPackageWithFalsifiedPaddingLength(int realPaddingBytes)
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

    public static MemoryStream AssemblyPackage(string id, string dllSourcePath, string version = "1.0.0")
    {
        var buffer = new MemoryStream();

        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry(PluginManifestValidator.FileName).Open(), Encoding.UTF8))
            {
                writer.Write(
                    $$"""
                    { "id": "{{id}}", "name": "Demo Binary", "version": "{{version}}", "kind": "assembly" }
                    """);
            }

            zip.CreateEntryFromFile(dllSourcePath, id + ".dll");
        }

        buffer.Position = 0;
        return buffer;
    }

    private static void Write(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8);
        writer.Write(content);
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
}
