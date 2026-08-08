using MqttProbe.Models.Plugins;
using MqttProbe.Services.Plugins.Packaging;

namespace MqttProbe.Shared.Tests.Services.Plugins.Packaging;

[TestFixture]
public class PluginManifestValidatorTests
{
    private static PluginPackageManifest Valid() => new()
    {
        Id = "chirpstack",
        Name = "ChirpStack Protobuf Schemas",
        Version = "1.0.0",
        Kind = PluginPackageKinds.ProtobufSchemas
    };

    [Test]
    public void Accepts_A_Well_Formed_Manifest()
    {
        PluginManifestValidator.Validate(Valid(), "1.0.3").IsValid.Should().BeTrue();
    }

    [Test]
    public void Rejects_A_Null_Manifest()
    {
        var result = PluginManifestValidator.Validate(null, "1.0.3");

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("mqttprobe-plugin.json");
    }

    [TestCase("")]
    [TestCase("Chirpstack")]
    [TestCase("-leading-dash")]
    [TestCase("has space")]
    [TestCase("has/slash")]
    [TestCase("..")]
    public void Rejects_Ids_That_Are_Not_Safe_Directory_Names(string id)
    {
        var result = PluginManifestValidator.Validate(Valid() with { Id = id }, "1.0.3");

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("id");
    }

    [TestCase("1.0")]
    [TestCase("v1.0.0")]
    [TestCase("")]
    [TestCase("1.0.0.0")]
    public void Rejects_Versions_That_Are_Not_Three_Part_SemVer(string version)
    {
        PluginManifestValidator.Validate(Valid() with { Version = version }, "1.0.3")
            .IsValid.Should().BeFalse();
    }

    [TestCase("1.0.0")]
    [TestCase("1.0.0-beta.1")]
    [TestCase("1.0.0+build5")]
    public void Accepts_SemVer_With_Prerelease_And_Build_Metadata(string version)
    {
        PluginManifestValidator.Validate(Valid() with { Version = version }, "1.0.3")
            .IsValid.Should().BeTrue();
    }

    [Test]
    public void Rejects_An_Unknown_Kind()
    {
        var result = PluginManifestValidator.Validate(Valid() with { Kind = "wasm" }, "1.0.3");

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("kind");
    }

    [Test]
    public void Rejects_A_Package_Requiring_A_Newer_App()
    {
        var result = PluginManifestValidator.Validate(
            Valid() with { MinAppVersion = "2.0.0" }, "1.0.3");

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("2.0.0");
    }

    [Test]
    public void Accepts_A_Package_Whose_Minimum_Is_Satisfied()
    {
        PluginManifestValidator.Validate(Valid() with { MinAppVersion = "1.0.3" }, "1.0.3+abc123")
            .IsValid.Should().BeTrue();
    }

    [Test]
    public void Deserialize_Reads_Camel_Case_Json()
    {
        var manifest = PluginManifestValidator.Deserialize(
            """
            { "id": "demo", "name": "Demo", "version": "2.1.0", "kind": "assembly" }
            """);

        manifest.Should().NotBeNull();
        manifest.Id.Should().Be("demo");
        manifest.Kind.Should().Be(PluginPackageKinds.Assembly);
    }

    [Test]
    public void Deserialize_Returns_Null_For_Malformed_Json()
    {
        PluginManifestValidator.Deserialize("{ not json").Should().BeNull();
    }
}
