using System.Globalization;
using System.Numerics;

namespace MqttProbe.Core.Services.Plugins.BuiltIn;

internal static class CborScalarFormatting
{
    private const int MaxNumericLiteralLength = 4_096;
    private const int MaxScaledExponent = 1_000;

    public static string FormatFloatingPoint(double value)
    {
        if (double.IsNaN(value))
            return "\"NaN\"";

        if (double.IsPositiveInfinity(value))
            return "\"Infinity\"";

        if (double.IsNegativeInfinity(value))
            return "\"-Infinity\"";

        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    public static string FormatDate(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);

    public static string? TryFormatScaledNumber(BigInteger mantissa, BigInteger exponent, int radix)
    {
        if (mantissa.IsZero)
            return "0";

        if (exponent > MaxScaledExponent || exponent < -MaxScaledExponent)
            return null;

        var power = (int)exponent;
        if (power >= 0)
        {
            var factor = radix == 10 ? BigInteger.Pow(10, power) : BigInteger.One << power;
            var integerText = (mantissa * factor).ToString(CultureInfo.InvariantCulture);
            return IsDecimalRepresentable(integerText) && integerText.Length <= MaxNumericLiteralLength
                ? integerText
                : null;
        }

        var scale = -power;
        var numerator = radix == 10 ? mantissa : mantissa * BigInteger.Pow(5, scale);
        var negative = numerator.Sign < 0;
        var digits = BigInteger.Abs(numerator).ToString(CultureInfo.InvariantCulture);

        if (digits.Length <= scale)
            digits = new string('0', scale - digits.Length + 1) + digits;

        var integerLength = digits.Length - scale;
        var text = digits[..integerLength] + "." + digits[integerLength..];
        text = text.TrimEnd('0').TrimEnd('.');
        if (negative)
            text = "-" + text;

        return IsDecimalRepresentable(text) && text.Length <= MaxNumericLiteralLength ? text : null;
    }

    private static bool IsDecimalRepresentable(string text)
    {
        var body = text.StartsWith('-') ? text[1..] : text;
        var dot = body.IndexOf('.');
        var integerPart = dot < 0 ? body : body[..dot];
        var fractionPart = dot < 0 ? string.Empty : body[(dot + 1)..];

        if (fractionPart.Length > 28 || integerPart.TrimStart('0').Length > 28)
            return false;

        return (integerPart + fractionPart).TrimStart('0').Length <= 29;
    }
}
