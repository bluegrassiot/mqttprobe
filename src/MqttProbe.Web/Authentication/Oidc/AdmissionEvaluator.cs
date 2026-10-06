using System.Text.Json;

namespace MqttProbe.Web.Authentication;

public static class AdmissionEvaluator
{
    public static bool IsAdmitted(
        ValidatedIdTokenPayload payload,
        string admissionClaim,
        string[] acceptedValues)
    {
        return IsAdmitted(payload.Root, admissionClaim, acceptedValues);
    }

    public static bool IsAdmitted(
        JsonElement payload,
        string admissionClaim,
        string[] acceptedValues)
    {
        if (!HasRequiredStringProperty(payload, AuthClaimTypes.Issuer) ||
            !HasRequiredStringProperty(payload, AuthClaimTypes.Subject))
        {
            return false;
        }

        if (!payload.TryGetProperty(admissionClaim, out var claimValue))
        {
            return false;
        }

        return EvaluateClaimValue(claimValue, acceptedValues);
    }

    public static string GetDisplayName(ValidatedIdTokenPayload payload)
    {
        return GetDisplayName(payload.Root);
    }

    public static string GetDisplayName(JsonElement payload)
    {
        var name = GetStringProperty(payload, AuthClaimTypes.Name);
        if (!string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        var preferredUsername = GetStringProperty(payload, AuthClaimTypes.PreferredUsername);
        if (!string.IsNullOrWhiteSpace(preferredUsername))
        {
            return preferredUsername;
        }

        return GetStringProperty(payload, AuthClaimTypes.Subject);
    }

    private static bool EvaluateClaimValue(JsonElement claimValue, string[] acceptedValues)
    {
        switch (claimValue.ValueKind)
        {
            case JsonValueKind.String:
                var str = claimValue.GetString();
                return str is not null && MatchesAny(str, acceptedValues);

            case JsonValueKind.Array:
                // First pass: verify ALL elements are strings (deny mixed arrays)
                foreach (var element in claimValue.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.String)
                    {
                        return false;
                    }
                }

                foreach (var element in claimValue.EnumerateArray())
                {
                    var item = element.GetString();
                    if (item is not null && MatchesAny(item, acceptedValues))
                    {
                        return true;
                    }
                }

                return false;

            default:
                return false;
        }
    }

    // Detects duplicate top-level JSON properties. System.Text.Json silently
    // keeps the last value, which is unsafe for security-sensitive payloads.
    public static bool HasDuplicateTopLevelProperties(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            return doc.RootElement.EnumerateObject().Any(prop => !seen.Add(prop.Name));
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private static bool HasRequiredStringProperty(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var element))
        {
            return false;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var value = element.GetString();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string GetStringProperty(JsonElement payload, string propertyName)
    {
        if (payload.TryGetProperty(propertyName, out var element) &&
            element.ValueKind == JsonValueKind.String)
        {
            return element.GetString() ?? "";
        }

        return "";
    }

    private static bool MatchesAny(string claimValue, string[] acceptedValues)
    {
        return acceptedValues.Any(accepted => string.Equals(claimValue, accepted, StringComparison.Ordinal));
    }
}
