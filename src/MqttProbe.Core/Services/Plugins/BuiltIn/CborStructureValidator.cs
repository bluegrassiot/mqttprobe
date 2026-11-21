using System.Buffers.Binary;
using System.Text.Unicode;

namespace MqttProbe.Core.Services.Plugins.BuiltIn;

internal static class CborStructureValidator
{
    internal const int MaxSupportedDepth = 60;
    private const int MaxItems = 1_000_000;

    public static bool TryValidate(ReadOnlySpan<byte> data)
    {
        var offset = 0;
        var items = 0;
        return TryReadItem(data, ref offset, 0, ref items) && offset == data.Length;
    }

    private static bool TryReadItem(ReadOnlySpan<byte> data, ref int offset, int depth, ref int items)
    {
        if (depth > MaxSupportedDepth || ++items > MaxItems)
            return false;

        if (!TryReadHeader(data, ref offset, out var major, out var argument, out var indefinite))
            return false;

        return major switch
        {
            0 or 1 => !indefinite,
            2 or 3 => TryReadString(data, ref offset, major, argument, indefinite),
            4 => TryReadArray(data, ref offset, depth, argument, indefinite, ref items),
            5 => TryReadMap(data, ref offset, depth, argument, indefinite, ref items),
            6 => !indefinite && TryReadItem(data, ref offset, depth + 1, ref items),
            _ => !indefinite
        };
    }

    private static bool TryReadString(
        ReadOnlySpan<byte> data, ref int offset, int major, ulong argument, bool indefinite)
    {
        if (!indefinite)
            return TryReadStringChunk(data, ref offset, major, argument);

        while (true)
        {
            if (offset >= data.Length)
                return false;

            if (data[offset] == 0xFF)
            {
                offset++;
                return true;
            }

            if (!TryReadHeader(data, ref offset, out var chunkMajor, out var chunkLength, out var chunkIndefinite) ||
                chunkMajor != major ||
                chunkIndefinite ||
                !TryReadStringChunk(data, ref offset, major, chunkLength))
            {
                return false;
            }
        }
    }

    private static bool TryReadStringChunk(
        ReadOnlySpan<byte> data, ref int offset, int major, ulong length)
    {
        if (length > (ulong)(data.Length - offset))
            return false;

        var content = data.Slice(offset, (int)length);
        if (major == 3 && !Utf8.IsValid(content))
            return false;

        offset += (int)length;
        return true;
    }

    private static bool TryReadArray(
        ReadOnlySpan<byte> data, ref int offset, int depth, ulong count, bool indefinite, ref int items)
    {
        if (indefinite)
        {
            while (true)
            {
                if (offset >= data.Length)
                    return false;

                if (data[offset] == 0xFF)
                {
                    offset++;
                    return true;
                }

                if (!TryReadItem(data, ref offset, depth + 1, ref items))
                    return false;
            }
        }

        if (count > (ulong)data.Length)
            return false;

        for (ulong i = 0; i < count; i++)
        {
            if (!TryReadItem(data, ref offset, depth + 1, ref items))
                return false;
        }

        return true;
    }

    private static bool TryReadMap(
        ReadOnlySpan<byte> data, ref int offset, int depth, ulong count, bool indefinite, ref int items)
    {
        if (indefinite)
        {
            while (true)
            {
                if (offset >= data.Length)
                    return false;

                if (data[offset] == 0xFF)
                {
                    offset++;
                    return true;
                }

                if (!TryReadItem(data, ref offset, depth + 1, ref items))
                    return false;

                if (!TryReadItem(data, ref offset, depth + 1, ref items))
                    return false;
            }
        }

        if (count > (ulong)(data.Length / 2))
            return false;

        for (ulong i = 0; i < count; i++)
        {
            if (!TryReadItem(data, ref offset, depth + 1, ref items))
                return false;

            if (!TryReadItem(data, ref offset, depth + 1, ref items))
                return false;
        }

        return true;
    }

    private static bool TryReadHeader(
        ReadOnlySpan<byte> data, ref int offset, out int major, out ulong argument, out bool indefinite)
    {
        major = 0;
        argument = 0;
        indefinite = false;

        if (offset >= data.Length)
            return false;

        var initial = data[offset++];
        major = initial >> 5;
        var additional = initial & 0x1F;

        if (additional < 24)
        {
            argument = (ulong)additional;
            return true;
        }

        if (additional == 31)
        {
            indefinite = true;
            return true;
        }

        var size = additional switch
        {
            24 => 1,
            25 => 2,
            26 => 4,
            27 => 8,
            _ => 0
        };

        if (size == 0 || offset + size > data.Length)
            return false;

        argument = size switch
        {
            1 => (ulong)data[offset],
            2 => BinaryPrimitives.ReadUInt16BigEndian(data[offset..]),
            4 => BinaryPrimitives.ReadUInt32BigEndian(data[offset..]),
            _ => BinaryPrimitives.ReadUInt64BigEndian(data[offset..])
        };
        offset += size;
        return true;
    }
}
