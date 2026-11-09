namespace MqttProbe.Core.Services.Sparkplug;

public static class SparkplugMetricValueConverter
{
    public static decimal DecodeInteger(uint datatype, ulong rawValue) => datatype switch
    {
        1 => unchecked((sbyte)rawValue),
        2 => unchecked((short)rawValue),
        3 => unchecked((int)rawValue),
        4 => unchecked((long)rawValue),
        5 => (byte)rawValue,
        6 => (ushort)rawValue,
        7 => (uint)rawValue,
        8 => rawValue,
        _ => rawValue
    };
}
