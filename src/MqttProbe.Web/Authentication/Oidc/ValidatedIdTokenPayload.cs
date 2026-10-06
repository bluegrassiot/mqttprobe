using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MqttProbe.Web.Authentication;

// Wrapper around validated ID-token payload. Callers cannot pass
// flattened claims or arbitrary dictionaries; this type is only created
// from an actual validated JsonWebToken or raw JSON with duplicate detection.
public sealed class ValidatedIdTokenPayload : IDisposable
{
    private readonly JsonDocument _document;

    private ValidatedIdTokenPayload(JsonDocument document)
    {
        _document = document;
    }

    public JsonElement Root => _document.RootElement;

    // Creates a ValidatedIdTokenPayload from an actual validated JsonWebToken.
    // Decodes the original EncodedPayload and rejects duplicate top-level properties.
    public static ValidatedIdTokenPayload CreateFromToken(JsonWebToken token)
    {
        ArgumentNullException.ThrowIfNull(token);

        var payloadJson = token.EncodedPayload;
        if (string.IsNullOrWhiteSpace(payloadJson))
            throw new ArgumentException("Token payload must not be blank.", nameof(token));

        var payloadBytes = Base64UrlEncoder.DecodeBytes(payloadJson);
        var json = System.Text.Encoding.UTF8.GetString(payloadBytes);

        return CreateFromRawJsonInternal(json, nameof(token));
    }

    // Internal test seam: creates from raw JSON text with duplicate detection.
    internal static ValidatedIdTokenPayload CreateFromRawJson(string rawPayloadJson)
    {
        if (string.IsNullOrWhiteSpace(rawPayloadJson))
            throw new ArgumentException("Payload JSON must not be blank.", nameof(rawPayloadJson));

        return CreateFromRawJsonInternal(rawPayloadJson, nameof(rawPayloadJson));
    }

    private static ValidatedIdTokenPayload CreateFromRawJsonInternal(string json, string paramName)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("Payload JSON is not valid.", paramName, ex);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var duplicate = doc.RootElement.EnumerateObject().FirstOrDefault(p => !seen.Add(p.Name));
        if (duplicate.Value.ValueKind != JsonValueKind.Undefined)
        {
            var name = duplicate.Name;
            doc.Dispose();
            throw new ArgumentException(
                $"Payload JSON contains duplicate top-level property '{name}'.",
                paramName);
        }

        return new ValidatedIdTokenPayload(doc);
    }

    public void Dispose()
    {
        _document.Dispose();
    }
}
