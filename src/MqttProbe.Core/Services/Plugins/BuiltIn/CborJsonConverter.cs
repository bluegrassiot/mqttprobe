using System.Formats.Cbor;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace MqttProbe.Core.Services.Plugins.BuiltIn;

internal sealed class CborJsonConverter
{
    private const int MaxOutputLength = 4_000_000;
    private const int MaxItemCount = 1_000_000;
    private const int MaxMapEntries = 100_000;
    private const int MaxJsonDepth = 64;

    private readonly CborReader _reader;
    private StringBuilder _output = new();
    private int _itemCount;
    private int _jsonDepth;
    private int _maxJsonDepth;
    private int _producedChars;

    private CborJsonConverter(ReadOnlyMemory<byte> data) =>
        _reader = new CborReader(data, CborConformanceMode.Lax);

    public static string Convert(ReadOnlyMemory<byte> data)
    {
        if (!CborStructureValidator.TryValidate(data.Span))
            throw new CborContentException("Payload is not a single valid CBOR data item.");

        var converter = new CborJsonConverter(data);
        converter.WriteValue(0);

        if (converter._reader.BytesRemaining != 0)
            throw new CborContentException("CBOR payload has trailing data.");

        return converter._output.ToString();
    }

    private void WriteValue(int depth)
    {
        if (depth > CborStructureValidator.MaxSupportedDepth)
            throw new CborContentException("CBOR nesting is too deep.");

        if (++_itemCount > MaxItemCount)
            throw new CborContentException("CBOR payload has too many items.");

        var state = _reader.PeekState();
        switch (state)
        {
            case CborReaderState.UnsignedInteger:
                Append(_reader.ReadUInt64().ToString(CultureInfo.InvariantCulture));
                return;

            case CborReaderState.NegativeInteger:
                Append(ReadNegativeInteger().ToString(CultureInfo.InvariantCulture));
                return;

            case CborReaderState.ByteString:
            case CborReaderState.StartIndefiniteLengthByteString:
                WriteJsonString(System.Convert.ToBase64String(_reader.ReadByteString()));
                return;

            case CborReaderState.TextString:
            case CborReaderState.StartIndefiniteLengthTextString:
                WriteJsonString(_reader.ReadTextString());
                return;

            case CborReaderState.StartArray:
                WriteArray(depth);
                return;

            case CborReaderState.StartMap:
                WriteMap(depth);
                return;

            case CborReaderState.Tag:
                WriteTag(depth);
                return;

            case CborReaderState.HalfPrecisionFloat:
            case CborReaderState.SinglePrecisionFloat:
            case CborReaderState.DoublePrecisionFloat:
                Append(CborScalarFormatting.FormatFloatingPoint(ReadFloatingPoint(state)));
                return;

            case CborReaderState.Boolean:
            case CborReaderState.Null:
            case CborReaderState.Undefined:
            case CborReaderState.SimpleValue:
                WriteSimpleState(state);
                return;

            default:
                throw new CborContentException($"Unsupported CBOR state '{state}'.");
        }
    }

    private double ReadFloatingPoint(CborReaderState state) => state switch
    {
        CborReaderState.HalfPrecisionFloat => (double)_reader.ReadHalf(),
        CborReaderState.SinglePrecisionFloat => _reader.ReadSingle(),
        _ => _reader.ReadDouble()
    };

    private void WriteSimpleState(CborReaderState state)
    {
        switch (state)
        {
            case CborReaderState.Boolean:
                Append(_reader.ReadBoolean() ? "true" : "false");
                break;
            case CborReaderState.Null:
                _reader.ReadNull();
                Append("null");
                break;
            case CborReaderState.Undefined:
                _reader.ReadSimpleValue();
                WriteCborMarker("undefined");
                break;
            default:
                WriteSimpleValue();
                break;
        }
    }

    private void WriteArray(int depth)
    {
        OpenContainer("[");
        var count = _reader.ReadStartArray();
        var first = true;

        if (count is { } definiteCount)
        {
            for (var i = 0; i < definiteCount; i++)
            {
                WriteSeparator(ref first);
                WriteValue(depth + 1);
            }
        }
        else
        {
            while (_reader.PeekState() != CborReaderState.EndArray)
            {
                WriteSeparator(ref first);
                WriteValue(depth + 1);
            }
        }

        _reader.ReadEndArray();
        CloseContainer("]");
    }

    private void WriteMap(int depth)
    {
        OpenContainer("{");
        var count = _reader.ReadStartMap();
        var entries = new List<CborMapEntry>();

        if (count is { } definiteCount)
        {
            for (var i = 0; i < definiteCount; i++)
                AddMapEntry(entries, depth);
        }
        else
        {
            while (_reader.PeekState() != CborReaderState.EndMap)
                AddMapEntry(entries, depth);
        }

        _reader.ReadEndMap();

        var textKeys = new HashSet<string>(StringComparer.Ordinal);
        var useObject = true;
        for (var i = 0; i < entries.Count && useObject; i++)
            useObject = entries[i].TextKey is { } textKey && textKeys.Add(textKey);

        if (useObject)
        {
            var first = true;
            foreach (var entry in entries)
            {
                WriteSeparator(ref first);
                AppendCaptured(entry.Key);
                Append(":");
                AppendCaptured(entry.Value);
            }
        }
        else
        {
            Append("\"$cbor\":\"map\",");
            OpenContainer("\"entries\":[");
            for (var i = 0; i < entries.Count; i++)
            {
                if (i > 0)
                    Append(",");
                OpenContainer("[");
                AppendCaptured(entries[i].Key);
                Append(",");
                AppendCaptured(entries[i].Value);
                CloseContainer("]");
            }
            CloseContainer("]");
        }

        CloseContainer("}");
    }

    private void OpenContainer(string text)
    {
        if (++_jsonDepth > MaxJsonDepth)
            throw new CborContentException("CBOR JSON nesting is too deep for the payload browser.");

        _maxJsonDepth = Math.Max(_maxJsonDepth, _jsonDepth);
        Append(text);
    }

    private void CloseContainer(string text)
    {
        Append(text);
        _jsonDepth--;
    }

    private void AddMapEntry(List<CborMapEntry> entries, int depth)
    {
        if (entries.Count >= MaxMapEntries)
            throw new CborContentException("CBOR map has too many entries.");

        entries.Add(ReadMapEntry(depth));
    }

    private CborMapEntry ReadMapEntry(int depth)
    {
        if (_reader.PeekState() is CborReaderState.TextString or CborReaderState.StartIndefiniteLengthTextString)
        {
            var text = _reader.ReadTextString();
            var keyJson = CaptureJson(JsonSerializer.Serialize(text));
            return new CborMapEntry(text, keyJson, CaptureValue(depth + 1));
        }

        var key = CaptureValue(depth + 1);
        var value = CaptureValue(depth + 1);
        return new CborMapEntry(null, key, value);
    }

    private void WriteTag(int depth)
    {
        var tag = _reader.PeekTag();
        var tagValue = tag is { } value ? (ulong)value : 0;

        if (tagValue is 2 or 3)
        {
            Append(_reader.ReadBigInteger().ToString(CultureInfo.InvariantCulture));
            return;
        }

        _reader.ReadTag();

        switch (tagValue)
        {
            case 55799:
                WriteValue(depth + 1);
                return;

            case 0:
                WriteTaggedDate(depth, tagValue, isUnixTime: false);
                return;

            case 1:
                WriteTaggedDate(depth, tagValue, isUnixTime: true);
                return;

            case 4:
                WriteScaledNumber(tagValue, radix: 10);
                return;

            case 5:
                WriteScaledNumber(tagValue, radix: 2);
                return;

            default:
                WriteTaggedValue(tagValue, CaptureValue(depth + 1));
                return;
        }
    }

    private void WriteTaggedDate(int depth, ulong tag, bool isUnixTime)
    {
        if (!isUnixTime && _reader.PeekState() == CborReaderState.TextString)
        {
            var text = _reader.ReadTextString();
            if (DateTimeOffset.TryParse(
                    text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            {
                WriteJsonString(CborScalarFormatting.FormatDate(parsed));
                return;
            }

            WriteTaggedValue(tag, CaptureJson(JsonSerializer.Serialize(text)));
            return;
        }

        var state = _reader.PeekState();
        if (state is not (CborReaderState.UnsignedInteger or CborReaderState.NegativeInteger
            or CborReaderState.HalfPrecisionFloat or CborReaderState.SinglePrecisionFloat
            or CborReaderState.DoublePrecisionFloat))
        {
            WriteTaggedValue(tag, CaptureValue(depth + 1));
            return;
        }

        var seconds = ReadDateSeconds();
        try
        {
            WriteJsonString(CborScalarFormatting.FormatDate(DateTimeOffset.UnixEpoch.AddSeconds(seconds)));
        }
        catch (ArgumentOutOfRangeException)
        {
            WriteTaggedValue(tag, CaptureJson(seconds.ToString("R", CultureInfo.InvariantCulture)));
        }
    }

    private double ReadDateSeconds()
    {
        var state = _reader.PeekState();
        if (state is CborReaderState.HalfPrecisionFloat or CborReaderState.SinglePrecisionFloat
            or CborReaderState.DoublePrecisionFloat)
        {
            return ReadFloatingPoint(state);
        }

        var integer = ReadInteger();
        return (double)integer;
    }

    private void WriteScaledNumber(ulong tag, int radix)
    {
        var count = _reader.ReadStartArray();
        if (count is not null and not 2)
            throw new CborContentException($"CBOR tag {tag} requires a two-element array.");

        var exponent = ReadInteger();
        var mantissa = ReadInteger();

        if (count is null && _reader.PeekState() != CborReaderState.EndArray)
            throw new CborContentException($"CBOR tag {tag} requires a two-element array.");

        _reader.ReadEndArray();

        var literal = CborScalarFormatting.TryFormatScaledNumber(mantissa, exponent, radix);
        if (literal is not null)
        {
            Append(literal);
            return;
        }

        OpenContainer("{");
        Append("\"tag\":");
        Append(tag.ToString(CultureInfo.InvariantCulture));
        Append(",\"exponent\":");
        Append(exponent.ToString(CultureInfo.InvariantCulture));
        Append(",\"mantissa\":");
        Append(mantissa.ToString(CultureInfo.InvariantCulture));
        CloseContainer("}");
    }

    private void WriteTaggedValue(ulong tag, CapturedJson valueJson)
    {
        OpenContainer("{");
        Append("\"tag\":");
        Append(tag.ToString(CultureInfo.InvariantCulture));
        Append(",\"value\":");
        AppendCaptured(valueJson);
        CloseContainer("}");
    }

    private void WriteSimpleValue()
    {
        var value = _reader.ReadSimpleValue();
        if (value == CborSimpleValue.Undefined)
        {
            WriteCborMarker("undefined");
            return;
        }

        OpenContainer("{");
        Append("\"$cbor\":\"simple\",\"value\":");
        Append(((byte)value).ToString(CultureInfo.InvariantCulture));
        CloseContainer("}");
    }

    private void WriteCborMarker(string name)
    {
        OpenContainer("{");
        Append("\"$cbor\":");
        WriteJsonString(name);
        CloseContainer("}");
    }

    private BigInteger ReadInteger()
    {
        var state = _reader.PeekState();
        return state switch
        {
            CborReaderState.UnsignedInteger => new BigInteger(_reader.ReadUInt64()),
            CborReaderState.NegativeInteger => ReadNegativeInteger(),
            _ when _reader.PeekTag() is { } tag && ((ulong)tag is 2 or 3) => _reader.ReadBigInteger(),
            _ => throw new CborContentException("Expected a CBOR integer.")
        };
    }

    private BigInteger ReadNegativeInteger()
    {
        var encoded = _reader.ReadCborNegativeIntegerRepresentation();
        return encoded <= long.MaxValue
            ? new BigInteger(-1L - (long)encoded)
            : -BigInteger.One - encoded;
    }

    private CapturedJson CaptureValue(int depth)
    {
        var previous = _output;
        var previousJsonDepth = _jsonDepth;
        var previousMaxJsonDepth = _maxJsonDepth;
        var baseDepth = _jsonDepth;
        _output = new StringBuilder();
        _maxJsonDepth = baseDepth;
        try
        {
            WriteValue(depth);
            return new CapturedJson(_output.ToString(), _maxJsonDepth - baseDepth);
        }
        finally
        {
            _maxJsonDepth = Math.Max(previousMaxJsonDepth, _maxJsonDepth);
            _jsonDepth = previousJsonDepth;
            _output = previous;
        }
    }

    private void WriteSeparator(ref bool first)
    {
        if (!first)
            Append(",");
        first = false;
    }

    private void WriteJsonString(string value) => Append(JsonSerializer.Serialize(value));

    private CapturedJson CaptureJson(string json)
    {
        Charge(json.Length);
        return new CapturedJson(json, 0);
    }

    private void Append(string text)
    {
        Charge(text.Length);
        _output.Append(text);
    }

    private void AppendCaptured(CapturedJson captured)
    {
        if (captured.RelativeMaxDepth > MaxJsonDepth - _jsonDepth)
            throw new CborContentException("CBOR JSON nesting is too deep for the payload browser.");

        if (captured.Json.Length > MaxOutputLength - _output.Length)
            throw new CborContentException("CBOR JSON output is too large.");

        _maxJsonDepth = Math.Max(_maxJsonDepth, _jsonDepth + captured.RelativeMaxDepth);
        _output.Append(captured.Json);
    }

    private void Charge(int length)
    {
        if (length > MaxOutputLength - _producedChars)
            throw new CborContentException("CBOR JSON output is too large.");

        _producedChars += length;
    }

    private sealed record CborMapEntry(string? TextKey, CapturedJson Key, CapturedJson Value);

    private readonly record struct CapturedJson(string Json, int RelativeMaxDepth);
}
