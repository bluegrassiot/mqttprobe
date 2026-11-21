using System.Text;
using MqttProbe.Core.Services.Mqtt;

namespace MqttProbe.UI.Utilities;

public enum CorrelationDataEncoding
{
    Text,
    Hex
}

public static class CorrelationDataCodec
{
    public const string HexError = "Correlation data must be an even number of hex digits.";
    public const string LengthError = "Correlation data cannot exceed 65,535 bytes.";

    public static bool TryParse(string? text, CorrelationDataEncoding encoding, out byte[] bytes) =>
        TryParse(text, encoding, out bytes, out _);

    public static bool TryParse(string? text, CorrelationDataEncoding encoding, out byte[] bytes, out string? error)
    {
        error = null;
        if (string.IsNullOrEmpty(text))
        {
            bytes = [];
            return true;
        }

        if (encoding == CorrelationDataEncoding.Text)
        {
            bytes = Encoding.UTF8.GetBytes(text);
        }
        else
        {
            var digits = NormalizeHex(text);
            if (digits.Length == 0 || digits.Length % 2 != 0 || !digits.All(char.IsAsciiHexDigit))
            {
                bytes = [];
                error = HexError;
                return false;
            }

            bytes = Convert.FromHexString(digits);
        }

        if (bytes.Length <= MqttStringRules.MaxBinaryBytes)
            return true;

        bytes = [];
        error = LengthError;
        return false;
    }

    public static (CorrelationDataEncoding Encoding, string Text) Format(byte[]? bytes)
    {
        if (bytes is not { Length: > 0 })
            return (CorrelationDataEncoding.Text, string.Empty);

        return MqttMessagePropertyDisplay.TryDecodePrintable(bytes, out var text)
            ? (CorrelationDataEncoding.Text, text)
            : (CorrelationDataEncoding.Hex, Convert.ToHexStringLower(bytes));
    }

    private static string NormalizeHex(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[2..];

        return trimmed.Replace(" ", "", StringComparison.Ordinal)
            .Replace(":", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal);
    }
}
