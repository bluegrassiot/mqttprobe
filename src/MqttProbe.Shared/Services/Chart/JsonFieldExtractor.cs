using System.Globalization;
using System.Text.Json;

namespace MqttProbe.Services.Chart;

public record ExtractedField(double Value, string? ContextJson = null);

public interface IJsonFieldExtractor
{
    public IReadOnlyDictionary<string, ExtractedField> Extract(string jsonPayload);
}

public class JsonFieldExtractor : IJsonFieldExtractor
{
    public IReadOnlyDictionary<string, ExtractedField> Extract(string jsonPayload)
    {
        var result = new Dictionary<string, ExtractedField>();
        if (string.IsNullOrWhiteSpace(jsonPayload)) return result;
        try
        {
            using var doc = JsonDocument.Parse(jsonPayload);
            Walk(doc.RootElement, "", result);
        }
        catch
        {
            // Invalid JSON — return empty
        }
        return result;
    }

    private static void Walk(JsonElement element, string prefix, Dictionary<string, ExtractedField> result)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                WalkObject(element, prefix, result);
                break;

            case JsonValueKind.Array:
                WalkArray(element, prefix, result);
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

    private static void WalkObject(JsonElement element, string prefix, Dictionary<string, ExtractedField> result)
    {
        foreach (var prop in element.EnumerateObject())
            Walk(prop.Value, BuildChildPath(prefix, prop.Name), result);
    }

    private static void WalkArray(JsonElement element, string prefix, Dictionary<string, ExtractedField> result)
    {
        if (TryExtractNamedValueArray(element, out var pairs))
        {
            foreach (var (name, value, contextJson) in pairs)
                result[BuildChildPath(prefix, name)] = new ExtractedField(value, contextJson);

            return;
        }

        var index = 0;
        foreach (var item in element.EnumerateArray())
            Walk(item, $"{prefix}[{index++}]", result);
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
