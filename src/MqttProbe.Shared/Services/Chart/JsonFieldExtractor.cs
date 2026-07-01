using System.Globalization;
using System.Text.Json;

namespace MqttProbe.Services.Chart;

public record ExtractedField(double Value, string? ContextJson = null);

public interface IJsonFieldExtractor
{
    public IReadOnlyDictionary<string, ExtractedField> Extract(string jsonPayload);
    public IReadOnlyDictionary<string, ExtractedField> Extract(string jsonPayload, IReadOnlyDictionary<ulong, string>? aliasNames);
}

public class JsonFieldExtractor : IJsonFieldExtractor
{
    public IReadOnlyDictionary<string, ExtractedField> Extract(string jsonPayload) =>
        Extract(jsonPayload, null);

    public IReadOnlyDictionary<string, ExtractedField> Extract(
        string jsonPayload, IReadOnlyDictionary<ulong, string>? aliasNames)
    {
        var result = new Dictionary<string, ExtractedField>();
        if (string.IsNullOrWhiteSpace(jsonPayload)) return result;

        // Skip any leading non-JSON prefix (e.g. "--" framing markers)
        var jsonStart = jsonPayload.IndexOfAny(['{', '[']);
        if (jsonStart < 0) return result;
        if (jsonStart > 0) jsonPayload = jsonPayload[jsonStart..];

        try
        {
            using var doc = JsonDocument.Parse(jsonPayload);
            Walk(doc.RootElement, "", result, aliasNames);
        }
        catch
        {
            // Invalid JSON — return empty
        }
        return result;
    }

    private static void Walk(
        JsonElement element, string prefix,
        Dictionary<string, ExtractedField> result,
        IReadOnlyDictionary<ulong, string>? aliasNames)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                WalkObject(element, prefix, result, aliasNames);
                break;

            case JsonValueKind.Array:
                WalkArray(element, prefix, result, aliasNames);
                break;

            case JsonValueKind.Number:
                AddNumericField(element, prefix, result);
                break;

            case JsonValueKind.String:
                AddStringNumericField(element, prefix, result);
                break;

            case JsonValueKind.True:
                AddBooleanField(prefix, result, true);
                break;

            case JsonValueKind.False:
                AddBooleanField(prefix, result, false);
                break;
        }
    }

    private static void WalkObject(
        JsonElement element, string prefix,
        Dictionary<string, ExtractedField> result,
        IReadOnlyDictionary<ulong, string>? aliasNames)
    {
        foreach (var prop in element.EnumerateObject())
            Walk(prop.Value, BuildChildPath(prefix, prop.Name), result, aliasNames);
    }

    private static void WalkArray(
        JsonElement element, string prefix,
        Dictionary<string, ExtractedField> result,
        IReadOnlyDictionary<ulong, string>? aliasNames)
    {
        if (TryExtractNamedValueArray(element, out var pairs))
        {
            foreach (var (name, value, contextJson) in pairs)
                result[BuildChildPath(prefix, name)] = new ExtractedField(value, contextJson);

            return;
        }

        if (aliasNames is not null
            && TryExtractAliasValueArray(element, aliasNames, out var aliasPairs))
        {
            foreach (var (name, value, contextJson) in aliasPairs)
                result[BuildChildPath(prefix, name)] = new ExtractedField(value, contextJson);

            return;
        }

        if (aliasNames is not null
            && TryExtractMixedValueArray(element, aliasNames, out var mixedPairs))
        {
            foreach (var (name, value, contextJson) in mixedPairs)
                result[BuildChildPath(prefix, name)] = new ExtractedField(value, contextJson);

            return;
        }

        var index = 0;
        foreach (var item in element.EnumerateArray())
            Walk(item, $"{prefix}[{index++}]", result, aliasNames);
    }

    private static string BuildChildPath(string prefix, string name) =>
        string.IsNullOrEmpty(prefix) ? name : $"{prefix}.{name}";

    private static void AddNumericField(JsonElement element, string prefix, Dictionary<string, ExtractedField> result)
    {
        if (!string.IsNullOrEmpty(prefix) && element.TryGetDouble(out var num))
            result[prefix] = new ExtractedField(num);
    }

    private static void AddStringNumericField(JsonElement element, string prefix, Dictionary<string, ExtractedField> result)
    {
        if (!string.IsNullOrEmpty(prefix)
            && element.GetString() is { } s
            && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
            result[prefix] = new ExtractedField(num);
    }

    private static void AddBooleanField(string prefix, Dictionary<string, ExtractedField> result, bool value)
    {
        if (!string.IsNullOrEmpty(prefix))
            result[prefix] = new ExtractedField(value ? 1.0 : 0.0);
    }

    private static bool TryExtractNamedValueArray(
        JsonElement array, out List<(string name, double value, string? contextJson)> pairs)
    {
        pairs = [];
        var sawNamedMetric = false;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                return false;

            if (!TryGetMetricName(item, out var name))
                return false;

            sawNamedMetric = true;

            if (!TryGetMetricValue(item, out var value))
                continue;

            pairs.Add((name, value, item.GetRawText()));
        }

        return sawNamedMetric;
    }

    private static bool TryExtractAliasValueArray(
        JsonElement array,
        IReadOnlyDictionary<ulong, string> aliasNames,
        out List<(string name, double value, string? contextJson)> pairs)
    {
        pairs = [];
        var sawAliasMetric = false;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                return false;

            if (!TryGetMetricAlias(item, out var alias))
                return false;

            if (!aliasNames.TryGetValue(alias, out var resolvedName))
                return false;

            sawAliasMetric = true;

            if (!TryGetMetricValue(item, out var value))
                continue;

            pairs.Add((resolvedName, value, item.GetRawText()));
        }

        return sawAliasMetric;
    }

    private static bool TryExtractMixedValueArray(
        JsonElement array,
        IReadOnlyDictionary<ulong, string> aliasNames,
        out List<(string name, double value, string? contextJson)> pairs)
    {
        pairs = [];
        var sawResolvable = false;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                return false;

            string? resolvedName = null;
            if (TryGetMetricName(item, out var metricName))
            {
                resolvedName = metricName;
            }
            else if (TryGetMetricAlias(item, out var alias)
                     && aliasNames.TryGetValue(alias, out var aliasName))
            {
                resolvedName = aliasName;
            }

            if (resolvedName is null)
                return false;

            sawResolvable = true;

            if (!TryGetMetricValue(item, out var value))
                continue;

            pairs.Add((resolvedName, value, item.GetRawText()));
        }

        return sawResolvable;
    }

    private static bool TryGetMetricAlias(JsonElement item, out ulong alias)
    {
        alias = 0;
        foreach (var prop in item.EnumerateObject())
        {
            if (prop is { Name: "alias", Value.ValueKind: JsonValueKind.Number }
                && prop.Value.TryGetUInt64(out var parsedAlias)
                && parsedAlias != 0)
            {
                alias = parsedAlias;
                return true;
            }
        }

        return false;
    }

    private static bool TryGetMetricName(JsonElement item, out string name)
    {
        name = string.Empty;
        foreach (var prop in item.EnumerateObject())
        {
            if (prop is not { Name: "name", Value.ValueKind: JsonValueKind.String })
                continue;

            var parsedName = prop.Value.GetString();
            if (parsedName is null)
                return false;

            name = parsedName;
            return true;
        }

        return false;
    }

    private static bool TryGetMetricValue(JsonElement item, out double value)
    {
        value = 0;
        foreach (var propValue in item.EnumerateObject().Where(prop => IsMetricValueFieldName(prop.Name)).Select(prop => prop.Value))
        {
            if (propValue.ValueKind == JsonValueKind.Number && propValue.TryGetDouble(out var parsedValue))
            {
                value = parsedValue;
                return true;
            }

            if (propValue.ValueKind == JsonValueKind.String
                && propValue.GetString() is { } stringValue
                && double.TryParse(stringValue, NumberStyles.Float, CultureInfo.InvariantCulture, out parsedValue))
            {
                value = parsedValue;
                return true;
            }

            if (propValue.ValueKind == JsonValueKind.True || propValue.ValueKind == JsonValueKind.False)
            {
                value = propValue.ValueKind == JsonValueKind.True ? 1.0 : 0.0;
                return true;
            }
        }

        return false;
    }

    private static bool IsMetricValueFieldName(string propertyName) =>
        propertyName is "value" or "int_value" or "float_value" or "double_value" or "long_value" or "boolean_value"
            or "intValue" or "floatValue" or "doubleValue" or "longValue" or "booleanValue";
}
