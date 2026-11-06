using System.Text.Json.Serialization;

namespace MqttProbe.UI.Components.Settings;

internal sealed class LegalCatalogue
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("components")]
    public List<LegalComponent> Components { get; set; } = [];

    [JsonPropertyName("documents")]
    public List<LegalDocument> Documents { get; set; } = [];
}

internal sealed class LegalComponent
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("license")]
    public string License { get; set; } = string.Empty;

    [JsonPropertyName("copyright")]
    public string Copyright { get; set; } = string.Empty;

    [JsonPropertyName("sourceUrl")]
    public string SourceUrl { get; set; } = string.Empty;

    [JsonPropertyName("documentIds")]
    public List<string> DocumentIds { get; set; } = [];
}

internal sealed class LegalDocument
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("file")]
    public string File { get; set; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    [JsonPropertyName("sourceUrls")]
    public List<string> SourceUrls { get; set; } = [];
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(LegalCatalogue))]
internal partial class LegalCatalogueJsonContext : JsonSerializerContext
{
}
