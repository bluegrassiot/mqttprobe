using MqttProbe.Services.Plugins.Protobuf;

namespace MqttProbe.Tests.Services.Plugins.Protobuf;

[TestFixture]
public class ProtobufSchemaFolderLoaderTests
{
    private static string NewPluginFolder() =>
        Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "mqttprobe-plugins-" + Guid.NewGuid().ToString("N"))).FullName;

    private static string WriteSchemaFolder(string pluginFolder, string manifestJson)
    {
        var schemaRoot = Directory.CreateDirectory(
            Path.Combine(pluginFolder, ProtobufSchemaFolderLoader.FolderName)).FullName;
        File.WriteAllText(
            Path.Combine(schemaRoot, ProtobufSchemaFolderLoader.ManifestFileName), manifestJson);
        return schemaRoot;
    }

    private const string ValidManifest = """
        {
          "schemas": [
            {
              "files": [ "integration/integration.proto" ],
              "topicPattern": "application/+/device/+/event/up",
              "messageType": "integration.UplinkEvent"
            }
          ]
        }
        """;

    [Test]
    public void Discovers_Manifest_In_Protobuf_Subfolder()
    {
        var pluginFolder = NewPluginFolder();
        var schemaRoot = WriteSchemaFolder(pluginFolder, ValidManifest);

        var sources = ProtobufSchemaFolderLoader.Discover([pluginFolder]);

        sources.Should().ContainSingle();
        sources[0].SchemaRoot.Should().Be(schemaRoot);
        var mapping = sources[0].Manifest.Schemas.Should().ContainSingle().Subject;
        mapping.Files.Should().ContainSingle().Which.Should().Be("integration/integration.proto");
        mapping.TopicPattern.Should().Be("application/+/device/+/event/up");
        mapping.MessageType.Should().Be("integration.UplinkEvent");
    }

    [Test]
    public void Plugin_Folder_Without_Protobuf_Subfolder_Yields_Nothing()
    {
        ProtobufSchemaFolderLoader.Discover([NewPluginFolder()]).Should().BeEmpty();
    }

    [Test]
    public void Missing_Plugin_Folder_Is_Skipped()
    {
        ProtobufSchemaFolderLoader.Discover([Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid())])
            .Should().BeEmpty();
    }

    [Test]
    public void Protobuf_Folder_Without_Manifest_Yields_Nothing()
    {
        var pluginFolder = NewPluginFolder();
        Directory.CreateDirectory(Path.Combine(pluginFolder, ProtobufSchemaFolderLoader.FolderName));

        ProtobufSchemaFolderLoader.Discover([pluginFolder]).Should().BeEmpty();
    }

    [Test]
    public void Malformed_Manifest_Is_Skipped_Without_Throwing()
    {
        var pluginFolder = NewPluginFolder();
        WriteSchemaFolder(pluginFolder, "{ this is not json");

        var act = () => ProtobufSchemaFolderLoader.Discover([pluginFolder]);

        act.Should().NotThrow();
        act().Should().BeEmpty();
    }

    [Test]
    public void Empty_Schemas_Array_Yields_Nothing()
    {
        var pluginFolder = NewPluginFolder();
        WriteSchemaFolder(pluginFolder, """{ "schemas": [] }""");

        ProtobufSchemaFolderLoader.Discover([pluginFolder]).Should().BeEmpty();
    }

    [Test]
    public void Manifest_Allows_Comments_And_Trailing_Commas()
    {
        var pluginFolder = NewPluginFolder();
        WriteSchemaFolder(pluginFolder, """
            {
              "schemas": [
                {
                  "files": [ "demo.proto" ],
                  "topicPattern": "sensors/+/data",
                  "messageType": "demo.Reading",
                },
              ],
            }
            """);

        ProtobufSchemaFolderLoader.Discover([pluginFolder]).Should().ContainSingle();
    }

    [Test]
    public void Property_Names_Are_Case_Insensitive()
    {
        var pluginFolder = NewPluginFolder();
        WriteSchemaFolder(pluginFolder, """
            {
              "Schemas": [
                { "Files": [ "demo.proto" ], "TopicPattern": "sensors/+/data", "MessageType": "demo.Reading" }
              ]
            }
            """);

        ProtobufSchemaFolderLoader.Discover([pluginFolder])
            .Should().ContainSingle().Which.Manifest.Schemas[0].MessageType.Should().Be("demo.Reading");
    }

    [Test]
    public void Discovers_Across_Multiple_Plugin_Folders()
    {
        var first = NewPluginFolder();
        var second = NewPluginFolder();
        WriteSchemaFolder(first, ValidManifest);
        WriteSchemaFolder(second, """
            { "schemas": [ { "files": [ "demo.proto" ], "topicPattern": "sensors/+/data", "messageType": "demo.Reading" } ] }
            """);

        ProtobufSchemaFolderLoader.Discover([first, second]).Should().HaveCount(2);
    }

    [Test]
    public void Duplicate_Plugin_Folder_Is_Loaded_Once()
    {
        var pluginFolder = NewPluginFolder();
        WriteSchemaFolder(pluginFolder, ValidManifest);

        ProtobufSchemaFolderLoader.Discover([pluginFolder, pluginFolder]).Should().ContainSingle();
    }

    [Test]
    public void Discovers_One_Source_Per_Subdirectory()
    {
        var pluginFolder = NewPluginFolder();
        var protobufFolder = Path.Combine(pluginFolder, ProtobufSchemaFolderLoader.FolderName);

        foreach (var vendor in new[] { "chirpstack", "acme" })
        {
            var vendorRoot = Directory.CreateDirectory(Path.Combine(protobufFolder, vendor)).FullName;
            File.WriteAllText(Path.Combine(vendorRoot, ProtobufSchemaFolderLoader.ManifestFileName), $$"""
                { "schemas": [ { "files": [ "a.proto" ], "topicPattern": "{{vendor}}/#", "messageType": "x.Y" } ] }
                """);
        }

        var sources = ProtobufSchemaFolderLoader.Discover([pluginFolder]);

        sources.Should().HaveCount(2);
        sources.Select(s => Path.GetFileName(s.SchemaRoot)).Should().BeEquivalentTo(["chirpstack", "acme"]);
    }

    [Test]
    public void Discovers_Root_And_Subdirectory_Sets_Together()
    {
        var pluginFolder = NewPluginFolder();
        var protobufFolder = WriteSchemaFolder(pluginFolder, ValidManifest);
        var vendorRoot = Directory.CreateDirectory(Path.Combine(protobufFolder, "acme")).FullName;
        File.WriteAllText(Path.Combine(vendorRoot, ProtobufSchemaFolderLoader.ManifestFileName),
            """{ "schemas": [ { "files": [ "a.proto" ], "topicPattern": "acme/#", "messageType": "x.Y" } ] }""");

        ProtobufSchemaFolderLoader.Discover([pluginFolder]).Should().HaveCount(2);
    }

    [Test]
    public void Subdirectory_Without_Manifest_Is_Ignored()
    {
        var pluginFolder = NewPluginFolder();
        var protobufFolder = WriteSchemaFolder(pluginFolder, ValidManifest);
        Directory.CreateDirectory(Path.Combine(protobufFolder, "integration"));

        ProtobufSchemaFolderLoader.Discover([pluginFolder]).Should().ContainSingle(
            "a .proto subdirectory of an existing set is not itself a schema set");
    }
}
