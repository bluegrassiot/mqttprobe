using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace MqttProbe.Web.Authentication;

internal static class BackChannelLogoutJson
{
    internal static bool HasBackChannelLogoutEvent(JsonElement payload)
    {
        return payload.TryGetProperty("events", out var events) &&
            events.ValueKind == JsonValueKind.Object &&
            events.TryGetProperty(BackChannelLogoutValidator.LogoutEventType, out var eventType) &&
            eventType.ValueKind == JsonValueKind.Object;
    }

    internal static bool ContainsDuplicateProperties(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var properties = element.EnumerateObject().ToList();
                var distinctNames = properties
                    .Select(property => property.Name)
                    .Distinct(StringComparer.Ordinal)
                    .Count();
                return distinctNames != properties.Count ||
                    properties.Any(property => ContainsDuplicateProperties(property.Value));

            case JsonValueKind.Array:
                return element.EnumerateArray().Any(ContainsDuplicateProperties);

            default:
                return false;
        }
    }

    internal static bool TryGetInt64(JsonElement payload, string name, out long value)
    {
        value = 0;
        return payload.TryGetProperty(name, out var element) &&
            element.ValueKind == JsonValueKind.Number &&
            element.TryGetInt64(out value);
    }

    internal static bool TryGetNonEmptyString(
        JsonElement payload,
        string name,
        [NotNullWhen(true)] out string? value)
    {
        value = null;
        if (!payload.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var candidate = element.GetString();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        value = candidate;
        return true;
    }

    // Optional claims must distinguish "absent" from "present but malformed":
    // only an absent sid may fall back to a subject-wide logout.
    internal static OptionalClaimState ReadOptionalStringClaim(
        JsonElement payload,
        string name,
        out string? value)
    {
        value = null;
        if (!payload.TryGetProperty(name, out var element))
        {
            return OptionalClaimState.Absent;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            return OptionalClaimState.Invalid;
        }

        var candidate = element.GetString();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return OptionalClaimState.Invalid;
        }

        value = candidate;
        return OptionalClaimState.Valid;
    }
}

internal enum OptionalClaimState
{
    Absent,
    Valid,
    Invalid
}
