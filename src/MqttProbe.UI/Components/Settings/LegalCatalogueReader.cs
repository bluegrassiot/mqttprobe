using System.Text.Json;

namespace MqttProbe.UI.Components.Settings;

public sealed class LegalCatalogueReader
{
    internal const string CatalogueResourceName = "MqttProbe.UI.Legal.catalogue.json";
    internal const string AppLicenseResourceName = "MqttProbe.UI.Legal.LICENSE";
    internal const string CatalogueErrorText = "The component licence catalogue is missing or couldn't be read in this build.";
    internal const string UnsupportedCatalogueErrorText = "The component licence catalogue uses an unsupported format.";
    internal const string AppLicenseErrorText = "The MQTTProbe licence text is missing from this build.";
    internal const string DocumentErrorText = "This licence text is missing from this build.";
    internal const string InvalidDocumentErrorText = "This licence text has an invalid document reference.";

    private const string DocumentResourcePrefix = "MqttProbe.UI.Legal.Texts.";

    private readonly Func<string, string?> _readResource;

    internal static LegalCatalogueReader Default { get; } = new(ReadEmbeddedResource);

    internal LegalCatalogueReader(Func<string, string?> readResource) =>
        _readResource = readResource ?? throw new ArgumentNullException(nameof(readResource));

    internal LegalCatalogueResult LoadCatalogue()
    {
        var json = _readResource(CatalogueResourceName);
        if (string.IsNullOrWhiteSpace(json))
            return new(null, CatalogueErrorText);

        try
        {
            var catalogue = JsonSerializer.Deserialize(json, LegalCatalogueJsonContext.Default.LegalCatalogue);
            if (catalogue is null)
                return new(null, CatalogueErrorText);
            if (catalogue.SchemaVersion != 1)
                return new(null, UnsupportedCatalogueErrorText);

            catalogue.Components ??= [];
            catalogue.Documents ??= [];
            foreach (var component in catalogue.Components)
                component.DocumentIds ??= [];
            foreach (var document in catalogue.Documents)
                document.SourceUrls ??= [];

            return new(catalogue, null);
        }
        catch (JsonException)
        {
            return new(null, CatalogueErrorText);
        }
    }

    internal LegalDocumentLoadResult LoadAppLicense()
    {
        var text = _readResource(AppLicenseResourceName);
        return string.IsNullOrWhiteSpace(text)
            ? new(null, AppLicenseErrorText)
            : new(Normalize(text), null);
    }

    internal LegalDocumentLoadResult LoadDocument(string documentId)
    {
        if (!IsSha256(documentId))
            return new(null, InvalidDocumentErrorText);

        var text = _readResource(DocumentResourceName(documentId));
        return string.IsNullOrWhiteSpace(text)
            ? new(null, DocumentErrorText)
            : new(Normalize(text), null);
    }

    internal static string DocumentResourceName(string documentId) => $"{DocumentResourcePrefix}{documentId}.txt";

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(char.IsAsciiHexDigit);

    private static string Normalize(string text) => text.ReplaceLineEndings("\n");

    private static string? ReadEmbeddedResource(string resourceName)
    {
        using var stream = typeof(LegalCatalogueReader).Assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
            return null;

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

internal sealed record LegalCatalogueResult(LegalCatalogue? Catalogue, string? Error);

internal sealed record LegalDocumentLoadResult(string? Text, string? Error);
