namespace MqttProbe.Core.Services.Sparkplug;

public static class SparkplugMetricValueConverter
{
    private static readonly string[] _labels =
    [
        "unknown",
        "int8",
        "int16",
        "int32",
        "int64",
        "uint8",
        "uint16",
        "uint32",
        "uint64",
        "float",
        "double",
        "bool",
        "string",
        "datetime",
        "text",
        "uuid",
        "dataset",
        "bytes",
        "file",
        "template",
        "propertyset",
        "propertysetlist",
        "int8[]",
        "int16[]",
        "int32[]",
        "int64[]",
        "uint8[]",
        "uint16[]",
        "uint32[]",
        "uint64[]",
        "float[]",
        "double[]",
        "bool[]",
        "string[]",
        "datetime[]"
    ];

    private static readonly Dictionary<string, uint> _datatypesByLabel = _labels
        .Select((label, index) => (label, index))
        .ToDictionary(item => item.label, item => (uint)item.index, StringComparer.Ordinal);

    public static string LabelForDatatype(uint datatype) =>
        datatype < (uint)_labels.Length ? _labels[(int)datatype] : "unknown";

    public static uint? DatatypeForLabel(string label) =>
        _datatypesByLabel.TryGetValue(label, out var datatype) ? datatype : null;

    public static bool IsIntegerDatatype(uint datatype) => datatype is >= 1 and <= 8;

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
