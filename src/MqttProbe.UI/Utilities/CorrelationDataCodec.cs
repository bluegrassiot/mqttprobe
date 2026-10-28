using System.Text;

namespace MqttProbe.UI.Utilities;

public enum CorrelationDataEncoding
{
    Text,
    Hex
}

public static class CorrelationDataCodec
{
    public static bool TryParse(string? text, CorrelationDataEncoding encoding, out byte[] bytes)
    {
        if (string.IsNullOrEmpty(text))
        {
            bytes = [];
            return true;
        }

        if (encoding == CorrelationDataEncoding.Text)
        {
            bytes = Encoding.UTF8.GetBytes(text);
            return true;
        }

        var digits = NormalizeHex(text);
        if (digits.Length == 0 || digits.Length % 2 != 0 || !digits.All(char.IsAsciiHexDigit))
        {
            bytes = [];
            return false;
        }

        bytes = Convert.FromHexString(digits);
        return true;
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
