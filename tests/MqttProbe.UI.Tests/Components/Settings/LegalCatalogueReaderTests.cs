using System.Security.Cryptography;
using System.Text;
using MqttProbe.UI.Components.Settings;

namespace MqttProbe.UI.Tests.Components.Settings;

[TestFixture]
public class LegalCatalogueReaderTests
{
    private static readonly string _documentA = new('a', 64);
    private static readonly string _documentB = new('b', 64);

    private static string CatalogueJson(int schemaVersion = 1) => $$"""
        {
          "schemaVersion": {{schemaVersion}},
          "generatedAt": "2026-11-04T00:00:00Z",
          "components": [
            {
              "id": "alpha",
              "name": "Alpha Core",
              "version": "1.2.3",
              "license": "MIT",
              "copyright": "Copyright 2024 Alpha",
              "sourceUrl": "https://example.com/alpha",
              "documentIds": ["{{_documentA}}", "{{_documentB}}"],
              "extra": "ignored"
            }
          ],
          "documents": [
            {
              "id": "{{_documentA}}",
              "title": "MIT Licence",
              "file": "{{_documentA}}.txt",
              "sha256": "{{_documentA}}",
              "sourceUrls": ["https://example.com/mit"],
              "extra": "ignored"
            }
          ]
        }
        """;

    [Test]
    public void Catalogue_Reads_The_Frozen_Contract_And_Ignores_Extra_Metadata()
    {
        var reader = ReaderFor(new()
        {
            [LegalCatalogueReader.CatalogueResourceName] = CatalogueJson()
        });

        var result = reader.LoadCatalogue();

        result.Error.Should().BeNull();
        result.Catalogue.Should().NotBeNull();
        result.Catalogue!.SchemaVersion.Should().Be(1);
        result.Catalogue.Components.Should().ContainSingle();
        result.Catalogue.Components[0].Name.Should().Be("Alpha Core");
        result.Catalogue.Components[0].DocumentIds.Should().Equal(_documentA, _documentB);
        result.Catalogue.Documents.Should().ContainSingle();
        result.Catalogue.Documents[0].Title.Should().Be("MIT Licence");
    }

    [Test]
    public void Missing_Catalogue_Returns_A_Visible_Error()
    {
        var result = ReaderFor(new()).LoadCatalogue();

        result.Catalogue.Should().BeNull();
        result.Error.Should().Be(LegalCatalogueReader.CatalogueErrorText);
    }

    [Test]
    public void Invalid_Json_Returns_A_Visible_Error()
    {
        var reader = ReaderFor(new()
        {
            [LegalCatalogueReader.CatalogueResourceName] = "{ not json"
        });

        reader.LoadCatalogue().Error.Should().Be(LegalCatalogueReader.CatalogueErrorText);
    }

    [Test]
    public void Unsupported_Schema_Is_Rejected()
    {
        var reader = ReaderFor(new()
        {
            [LegalCatalogueReader.CatalogueResourceName] = CatalogueJson(schemaVersion: 2)
        });

        reader.LoadCatalogue().Error.Should().Be(LegalCatalogueReader.UnsupportedCatalogueErrorText);
    }

    [Test]
    public void Non_Hash_Document_Id_Is_Rejected_Without_Reading_A_Resource()
    {
        var requested = new List<string>();
        var reader = new LegalCatalogueReader(name =>
        {
            requested.Add(name);
            return "should not be read";
        });

        var result = reader.LoadDocument("../../outside");

        result.Text.Should().BeNull();
        result.Error.Should().Be(LegalCatalogueReader.InvalidDocumentErrorText);
        requested.Should().BeEmpty();
    }

    [Test]
    public void Valid_Document_Id_Maps_To_The_Generated_Text_Resource()
    {
        var requested = new List<string>();
        var reader = new LegalCatalogueReader(name =>
        {
            requested.Add(name);
            return "MIT License\r\n";
        });

        var result = reader.LoadDocument(_documentA);

        result.Error.Should().BeNull();
        result.Text.Should().Be("MIT License\n");
        requested.Should().Equal(LegalCatalogueReader.DocumentResourceName(_documentA));
    }

    [Test]
    public void Missing_Document_Returns_A_Visible_Error()
    {
        var result = ReaderFor(new()).LoadDocument(_documentA);

        result.Text.Should().BeNull();
        result.Error.Should().Be(LegalCatalogueReader.DocumentErrorText);
    }

    [Test]
    public void Missing_App_Licence_Returns_A_Visible_Error()
    {
        var result = ReaderFor(new()).LoadAppLicense();

        result.Text.Should().BeNull();
        result.Error.Should().Be(LegalCatalogueReader.AppLicenseErrorText);
    }

    [Test]
    public void Embedded_Catalogue_Parses_And_App_Licence_Is_Available()
    {
        var catalogue = LegalCatalogueReader.Default.LoadCatalogue();

        catalogue.Error.Should().BeNull();
        catalogue.Catalogue.Should().NotBeNull();
        catalogue.Catalogue!.Components.Should().NotBeEmpty();
        LegalCatalogueReader.Default.LoadAppLicense().Error.Should().BeNull();
    }

    [Test]
    public void Embedded_Documents_Match_Their_Catalogue_Ids_And_Resource_Names()
    {
        var catalogue = LegalCatalogueReader.Default.LoadCatalogue().Catalogue;
        catalogue.Should().NotBeNull();

        var documentIds = catalogue!.Documents.Select(document => document.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var component in catalogue.Components)
        {
            foreach (var documentId in component.DocumentIds)
                documentIds.Should().Contain(documentId);
        }

        var resourceNames = typeof(LegalCatalogueReader).Assembly.GetManifestResourceNames();
        foreach (var document in catalogue.Documents)
        {
            document.File.Should().Be($"{document.Id}.txt");
            document.Sha256.Should().Be(document.Id);
            resourceNames.Should().Contain(LegalCatalogueReader.DocumentResourceName(document.Id));

            var content = LegalCatalogueReader.Default.LoadDocument(document.Id);
            content.Error.Should().BeNull();
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content.Text!))).ToLowerInvariant();
            hash.Should().Be(document.Id);
        }
    }

    private static LegalCatalogueReader ReaderFor(Dictionary<string, string> resources) =>
        new(name => resources.GetValueOrDefault(name));
}
