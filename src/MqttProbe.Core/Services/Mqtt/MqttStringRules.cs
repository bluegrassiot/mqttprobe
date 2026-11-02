using System.Text;

namespace MqttProbe.Core.Services.Mqtt;

public static class MqttStringRules
{
    // MQTT 5 caps UTF-8 strings and binary data at 65,535 bytes, and forbids U+0000 in strings (section 1.5.4).
    public const int MaxUtf8Bytes = 65_535;
    public const int MaxBinaryBytes = 65_535;

    public static bool IsValidUtf8String(string? value) =>
        value is not null &&
        !value.Contains('\0') &&
        Encoding.UTF8.GetByteCount(value) <= MaxUtf8Bytes;
}
