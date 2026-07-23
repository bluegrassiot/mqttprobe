extern alias ProtoReflection;

using System.Text;
using DescriptorProto = ProtoReflection::Google.Protobuf.Reflection.DescriptorProto;
using FieldDescriptorProto = ProtoReflection::Google.Protobuf.Reflection.FieldDescriptorProto;

namespace MqttProbe.Services.Plugins.Protobuf;

public sealed class ProtobufWireDecoder
{
    private const int MaxDepth = 100;

    private readonly ProtobufSchemaRegistry _registry;

    public ProtobufWireDecoder(ProtobufSchemaRegistry registry) => _registry = registry;

    public Dictionary<string, object?> Decode(ReadOnlySpan<byte> payload, DescriptorProto message) =>
        Decode(payload, message, 0);

    private Dictionary<string, object?> Decode(ReadOnlySpan<byte> payload, DescriptorProto message, int depth)
    {
        if (depth > MaxDepth)
            throw new InvalidDataException($"Protobuf nesting exceeds {MaxDepth} levels.");

        var fields = new Dictionary<int, FieldDescriptorProto>();
        foreach (var f in message.Fields)
            fields[f.Number] = f;

        var result = new Dictionary<string, object?>();
        var reader = new ProtobufWireReader(payload);

        while (reader.TryReadTag(out var number, out var wireType))
        {
            if (!fields.TryGetValue(number, out var field) || !IsWireTypeCompatible(field, wireType))
            {
                AddUnknown(result, $"field_{number}", ReadUnknown(ref reader, wireType));
                continue;
            }

            var isRepeated = field.label == FieldDescriptorProto.Label.LabelRepeated;
            var value = ReadFieldValue(ref reader, field, wireType, depth, out var isPackedList);

            if (isPackedList && value is List<object?> packed)
            {
                foreach (var item in packed)
                    AddValue(result, field.Name, item, forceList: isRepeated);
            }
            else
            {
                AddValue(result, field.Name, value, forceList: isRepeated);
            }
        }

        return result;
    }

    private object? ReadFieldValue(ref ProtobufWireReader reader, FieldDescriptorProto field, int wireType, int depth, out bool isPackedList)
    {
        isPackedList = false;

        // Packed repeated scalar: a repeated numeric field encoded length-delimited.
        if (IsPackedRepeated(field, wireType))
        {
            isPackedList = true;
            var packedSpan = reader.ReadLengthDelimited();
            var list = new List<object?>();
            var inner = new ProtobufWireReader(packedSpan);
            while (!inner.End)
                list.Add(ReadScalarFromWire(ref inner, field, ExpectedWireType(field.type)));
            return list;
        }

        return field.type switch
        {
            FieldDescriptorProto.Type.TypeMessage => ReadNestedMessage(ref reader, field, depth),
            _ => ReadScalarFromWire(ref reader, field, wireType),
        };
    }

    private object? ReadNestedMessage(ref ProtobufWireReader reader, FieldDescriptorProto field, int depth)
    {
        var span = reader.ReadLengthDelimited();
        if (_registry.TryResolveMessage(field.TypeName, out var nested))
            return Decode(span, nested, depth + 1);
        // Unresolved message type: preserve raw bytes as hex.
        return Convert.ToHexString(span);
    }

    private object? ReadScalarFromWire(ref ProtobufWireReader reader, FieldDescriptorProto field, int wireType)
    {
        switch (field.type)
        {
            case FieldDescriptorProto.Type.TypeInt32:
            case FieldDescriptorProto.Type.TypeInt64:
                return (long)reader.ReadVarint();
            case FieldDescriptorProto.Type.TypeUint32:
            case FieldDescriptorProto.Type.TypeUint64:
                return reader.ReadVarint();
            case FieldDescriptorProto.Type.TypeSint32:
            case FieldDescriptorProto.Type.TypeSint64:
                return ZigZag(reader.ReadVarint());
            case FieldDescriptorProto.Type.TypeBool:
                return reader.ReadVarint() != 0;
            case FieldDescriptorProto.Type.TypeEnum:
                var raw = (long)reader.ReadVarint();
                return ResolveEnumName(field, raw);
            case FieldDescriptorProto.Type.TypeFixed64:
                return reader.ReadFixed64();
            case FieldDescriptorProto.Type.TypeSfixed64:
                return unchecked((long)reader.ReadFixed64());
            case FieldDescriptorProto.Type.TypeDouble:
                return BitConverter.Int64BitsToDouble(unchecked((long)reader.ReadFixed64()));
            case FieldDescriptorProto.Type.TypeFixed32:
                return reader.ReadFixed32();
            case FieldDescriptorProto.Type.TypeSfixed32:
                return unchecked((int)reader.ReadFixed32());
            case FieldDescriptorProto.Type.TypeFloat:
                return BitConverter.Int32BitsToSingle(unchecked((int)reader.ReadFixed32()));
            case FieldDescriptorProto.Type.TypeString:
                return Encoding.UTF8.GetString(reader.ReadLengthDelimited());
            case FieldDescriptorProto.Type.TypeBytes:
                return Convert.ToBase64String(reader.ReadLengthDelimited());
            default:
                reader.SkipField(wireType);
                return null;
        }
    }

    private object ResolveEnumName(FieldDescriptorProto field, long value)
    {
        if (_registry.TryResolveEnum(field.TypeName, out var e))
        {
            foreach (var v in e.Values)
                if (v.Number == value)
                    return v.Name;
        }
        return value;
    }

    private static object ReadUnknown(ref ProtobufWireReader reader, int wireType) =>
        wireType switch
        {
            0 => reader.ReadVarint(),
            1 => reader.ReadFixed64(),
            2 => Convert.ToHexString(reader.ReadLengthDelimited()),
            5 => reader.ReadFixed32(),
            _ => throw new InvalidDataException($"Unsupported wire type {wireType}."),
        };

    private static void AddValue(Dictionary<string, object?> target, string key, object? value, bool forceList = false)
    {
        // Singular fields are last-one-wins: concatenating two serializations of
        // the same message is a legal protobuf idiom and must not change a
        // scalar into a list.
        if (!forceList)
        {
            target[key] = value;
            return;
        }
        if (target.TryGetValue(key, out var existing) && existing is List<object?> list)
        {
            list.Add(value);
            return;
        }
        var newList = new List<object?>();
        if (target.TryGetValue(key, out var prior))
            newList.Add(prior);
        newList.Add(value);
        target[key] = newList;
    }

    private static void AddUnknown(Dictionary<string, object?> target, string key, object? value)
    {
        if (!target.ContainsKey(key))
        {
            target[key] = value;
            return;
        }
        AddValue(target, key, value, forceList: true);
    }

    private static long ZigZag(ulong n) => (long)(n >> 1) ^ -(long)(n & 1);

    private static bool IsPackableScalar(FieldDescriptorProto.Type type) =>
        type is not (FieldDescriptorProto.Type.TypeString
            or FieldDescriptorProto.Type.TypeBytes
            or FieldDescriptorProto.Type.TypeMessage
            or FieldDescriptorProto.Type.TypeGroup);

    private static bool IsPackedRepeated(FieldDescriptorProto field, int wireType) =>
        wireType == 2
        && field.label == FieldDescriptorProto.Label.LabelRepeated
        && IsPackableScalar(field.type);

    private static bool IsWireTypeCompatible(FieldDescriptorProto field, int wireType) =>
        IsPackedRepeated(field, wireType) || wireType == ExpectedWireType(field.type);

    private static int ExpectedWireType(FieldDescriptorProto.Type type) => type switch
    {
        FieldDescriptorProto.Type.TypeFixed64 or FieldDescriptorProto.Type.TypeSfixed64 or FieldDescriptorProto.Type.TypeDouble => 1,
        FieldDescriptorProto.Type.TypeFixed32 or FieldDescriptorProto.Type.TypeSfixed32 or FieldDescriptorProto.Type.TypeFloat => 5,
        FieldDescriptorProto.Type.TypeString or FieldDescriptorProto.Type.TypeBytes or FieldDescriptorProto.Type.TypeMessage => 2,
        FieldDescriptorProto.Type.TypeGroup => 3,
        _ => 0,
    };
}
