using System.Text;
using MQTTnet.Protocol;

namespace MqttProbe.UI.Utilities;

public static class MqttMessagePropertyDisplay
{
    private static readonly UTF8Encoding _strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static string FormatPayloadFormat(MqttPayloadFormatIndicator indicator) => indicator switch
    {
        MqttPayloadFormatIndicator.CharacterData => "UTF-8 text",
        MqttPayloadFormatIndicator.Unspecified => "Unspecified",
        _ => indicator.ToString()
    };

    public static string FormatExpiry(uint seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        var parts = new List<string>(4);
        if (span.Days > 0) parts.Add($"{span.Days}d");
        if (span.Hours > 0) parts.Add($"{span.Hours}h");
        if (span.Minutes > 0) parts.Add($"{span.Minutes}m");
        if (span.Seconds > 0 || parts.Count == 0) parts.Add($"{span.Seconds}s");
        return seconds < 60 ? $"{seconds} s" : $"{seconds} s ({string.Join(' ', parts)})";
    }

    public static string FormatCorrelationData(byte[] data)
    {
        if (TryDecodePrintable(data, out var text))
            return text;

        return $"0x{Convert.ToHexStringLower(data)} ({data.Length} bytes)";
    }

    private static bool TryDecodePrintable(byte[] data, out string text)
    {
        try
        {
            text = _strictUtf8.GetString(data);
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }

        return !string.IsNullOrWhiteSpace(text) && !text.Any(char.IsControl);
    }
}
