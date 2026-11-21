using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Org.Eclipse.Tahu.Protobuf;

namespace MqttProbe.Core.Services.Sparkplug;

public static class SparkplugMetricValueEncoder
{
    private const string FiniteNumberError = "Enter a finite number, using . as the decimal separator.";

    public static bool IsWritable(string dataType) => dataType is
        "int8" or "int16" or "int32" or "int64"
        or "uint8" or "uint16" or "uint32" or "uint64"
        or "float" or "double" or "bool"
        or "string" or "text" or "uuid" or "datetime";

    public static bool TryEncode(
        string dataType, string? text, [NotNullWhen(true)] out Payload.Types.Metric? metric, out string error)
    {
        metric = null;
        if (!IsWritable(dataType) || SparkplugMetricValueConverter.DatatypeForLabel(dataType) is not { } datatype)
        {
            error = $"Metrics of type {dataType} cannot be written.";
            return false;
        }

        text ??= string.Empty;
        var encoded = new Payload.Types.Metric { Datatype = datatype };
        var failure = dataType switch
        {
            "int8" => SetSigned(encoded, text, sbyte.MinValue, sbyte.MaxValue),
            "int16" => SetSigned(encoded, text, short.MinValue, short.MaxValue),
            "int32" => SetSigned(encoded, text, int.MinValue, int.MaxValue),
            "int64" => SetSigned(encoded, text, long.MinValue, long.MaxValue),
            "uint8" => SetUnsigned(encoded, text, byte.MaxValue),
            "uint16" => SetUnsigned(encoded, text, ushort.MaxValue),
            "uint32" => SetUnsigned(encoded, text, uint.MaxValue),
            "uint64" => SetUnsigned(encoded, text, ulong.MaxValue),
            "float" => SetFloat(encoded, text),
            "double" => SetDouble(encoded, text),
            "bool" => SetBoolean(encoded, text),
            "datetime" => SetDateTime(encoded, text),
            "uuid" => SetUuid(encoded, text),
            _ => SetString(encoded, text)
        };

        if (failure is not null)
        {
            error = failure;
            return false;
        }

        metric = encoded;
        error = string.Empty;
        return true;
    }

    private static string? SetSigned(Payload.Types.Metric metric, string text, long min, long max)
    {
        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            || value < min || value > max)
            return string.Create(CultureInfo.InvariantCulture, $"Enter a whole number from {min} to {max}.");

        // Sparkplug carries signed integers as two's complement in the unsigned protobuf fields.
        if (min == long.MinValue)
            metric.LongValue = unchecked((ulong)value);
        else
            metric.IntValue = unchecked((uint)(int)value);
        return null;
    }

    private static string? SetUnsigned(Payload.Types.Metric metric, string text, ulong max)
    {
        if (!ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value > max)
            return string.Create(CultureInfo.InvariantCulture, $"Enter a whole number from 0 to {max}.");

        if (max == ulong.MaxValue)
            metric.LongValue = value;
        else
            metric.IntValue = (uint)value;
        return null;
    }

    private static string? SetFloat(Payload.Types.Metric metric, string text)
    {
        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || !float.IsFinite(value))
            return FiniteNumberError;

        metric.FloatValue = value;
        return null;
    }

    private static string? SetDouble(Payload.Types.Metric metric, string text)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value))
            return FiniteNumberError;

        metric.DoubleValue = value;
        return null;
    }

    private static string? SetBoolean(Payload.Types.Metric metric, string text)
    {
        if (!bool.TryParse(text, out var value))
            return "Choose true or false.";

        metric.BooleanValue = value;
        return null;
    }

    private static string? SetDateTime(Payload.Types.Metric metric, string text)
    {
        if (!ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            return "Enter Unix time in milliseconds.";

        metric.LongValue = value;
        return null;
    }

    private static string? SetUuid(Payload.Types.Metric metric, string text)
    {
        var trimmed = text.Trim();
        if (!Guid.TryParse(trimmed, out _))
            return "Enter a UUID, for example 123e4567-e89b-12d3-a456-426614174000.";

        metric.StringValue = trimmed;
        return null;
    }

    private static string? SetString(Payload.Types.Metric metric, string text)
    {
        metric.StringValue = text;
        return null;
    }
}
