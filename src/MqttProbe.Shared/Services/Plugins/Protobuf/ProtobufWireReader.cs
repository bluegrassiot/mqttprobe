namespace MqttProbe.Services.Plugins.Protobuf;

public ref struct ProtobufWireReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _pos;

    public ProtobufWireReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _pos = 0;
    }

    public readonly bool End => _pos >= _data.Length;

    public bool TryReadTag(out int fieldNumber, out int wireType)
    {
        if (End)
        {
            fieldNumber = 0;
            wireType = 0;
            return false;
        }
        var key = ReadVarint();
        fieldNumber = (int)(key >> 3);
        wireType = (int)(key & 0x7);
        return true;
    }

    public ulong ReadVarint()
    {
        ulong result = 0;
        var shift = 0;
        while (shift < 64)
        {
            if (_pos >= _data.Length)
                throw new InvalidDataException("Truncated varint.");
            var b = _data[_pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return result;
            shift += 7;
        }
        throw new InvalidDataException("Varint too long.");
    }

    public ulong ReadFixed64()
    {
        if (_pos + 8 > _data.Length)
            throw new InvalidDataException("Truncated fixed64.");
        var value = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(_data.Slice(_pos, 8));
        _pos += 8;
        return value;
    }

    public uint ReadFixed32()
    {
        if (_pos + 4 > _data.Length)
            throw new InvalidDataException("Truncated fixed32.");
        var value = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(_pos, 4));
        _pos += 4;
        return value;
    }

    public ReadOnlySpan<byte> ReadLengthDelimited()
    {
        var raw = ReadVarint();
        if (raw > (ulong)(_data.Length - _pos))
            throw new InvalidDataException("Truncated length-delimited field.");
        var len = (int)raw;
        var slice = _data.Slice(_pos, len);
        _pos += len;
        return slice;
    }

    public void SkipField(int wireType)
    {
        switch (wireType)
        {
            case 0: ReadVarint(); break;
            case 1: ReadFixed64(); break;
            case 2: ReadLengthDelimited(); break;
            case 5: ReadFixed32(); break;
            default: throw new InvalidDataException($"Unsupported wire type {wireType}.");
        }
    }
}
