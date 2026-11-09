using MQTTnet;
using MqttProbe.Core.Services.Plugins.BuiltIn;

namespace MqttProbe.Core.Tests.Services.Plugins.BuiltIn;

[TestFixture]
public class CborPayloadDecoderTests
{
    private static MqttApplicationMessageReceivedEventArgs MakeArgsBytes(string topic, byte[] payload)
    {
        var appMsg = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .Build();
        var packet = new MQTTnet.Packets.MqttPublishPacket { Topic = topic };
        return new MqttApplicationMessageReceivedEventArgs("test-client", appMsg, packet, null);
    }

    private static string Decode(byte[] payload) =>
        new CborPayloadDecoder().Decode(MakeArgsBytes("sensor/cbor", payload)).DisplayText;

    [Test]
    public void Decode_TextKeyedMap_ReturnsJsonObject()
    {
        var result = new CborPayloadDecoder().Decode(
            MakeArgsBytes("sensor/cbor", [0xA1, 0x61, 0x61, 0x01]));

        result.IsFailure.Should().BeFalse();
        result.FormatId.Should().Be("cbor");
        result.DisplayText.Should().Be("""{"a":1}""");
    }

    [Test]
    public void Decode_ArrayOfScalars_ReturnsJsonArray()
    {
        Decode([0x83, 0x01, 0x02, 0x03]).Should().Be("[1,2,3]");
    }

    [Test]
    public void Decode_ByteString_ReturnsBase64String()
    {
        Decode([0x43, 0x01, 0x02, 0x03]).Should().Be("\"AQID\"");
    }

    [Test]
    public void Decode_IndefiniteTextString_ConcatenatesChunks()
    {
        Decode([0x7F, 0x62, 0x68, 0x69, 0x62, 0x21, 0x21, 0xFF]).Should().Be("\"hi!!\"");
    }

    [Test]
    public void Decode_IndefiniteMap_ReturnsJsonObject()
    {
        Decode([0xBF, 0x61, 0x61, 0x01, 0x61, 0x62, 0x02, 0xFF])
            .Should().Be("""{"a":1,"b":2}""");
    }

    [Test]
    public void Decode_IndefiniteTextMapKey_ReturnsJsonObject()
    {
        Decode([0xBF, 0x7F, 0x61, 0x61, 0xFF, 0x01, 0xFF]).Should().Be("""{"a":1}""");
    }

    [Test]
    public void Decode_BooleanNullAndFloat_ReturnsJsonScalars()
    {
        Decode([0x83, 0xF5, 0xF6, 0xFB, 0x3F, 0xF1, 0x99, 0x99, 0x99, 0x99, 0x99, 0x9A])
            .Should().Be("[true,null,1.1]");
    }

    [Test]
    public void Decode_EmptyPayload_ReturnsEmptyDisplayText()
    {
        var result = new CborPayloadDecoder().Decode(MakeArgsBytes("sensor/cbor", []));

        result.IsFailure.Should().BeFalse();
        result.DisplayText.Should().BeEmpty();
    }

    [Test]
    public void Decode_TruncatedPayload_ReturnsHexFallback()
    {
        var result = new CborPayloadDecoder().Decode(
            MakeArgsBytes("sensor/cbor", [0xA2, 0x61, 0x61, 0x01]));

        result.IsFailure.Should().BeFalse();
        result.DisplayText.Should().Be("a2616101");
    }

    [Test]
    public void Decode_FullUInt64_ReturnsExactNumber()
    {
        Decode([0x1B, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF])
            .Should().Be("18446744073709551615");
    }

    [Test]
    public void Decode_NegativeIntegerBelowInt64_ReturnsExactNumber()
    {
        Decode([0x3B, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00])
            .Should().Be("-9223372036854775809");
    }

    [Test]
    public void Decode_PositiveBignumTag_ReturnsExactNumber()
    {
        Decode([0xC2, 0x49, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00])
            .Should().Be("18446744073709551616");
    }

    [Test]
    public void Decode_NegativeBignumTag_ReturnsExactNumber()
    {
        Decode([0xC3, 0x49, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00])
            .Should().Be("-18446744073709551617");
    }

    [Test]
    public void Decode_SelfDescribedTag_UnwrapsPayload()
    {
        Decode([0xD9, 0xD9, 0xF7, 0xA1, 0x61, 0x61, 0x01]).Should().Be("""{"a":1}""");
    }

    [Test]
    public void Decode_DateStringTag_ReturnsIsoString()
    {
        var payload = new byte[] { 0xC0, 0x74 }
            .Concat(System.Text.Encoding.ASCII.GetBytes("2013-03-21T20:04:00Z"))
            .ToArray();

        Decode(payload).Should().Be("\"2013-03-21T20:04:00Z\"");
    }

    [Test]
    public void Decode_EpochDateTag_ReturnsIsoString()
    {
        Decode([0xC1, 0x1A, 0x51, 0x4B, 0x67, 0xB0])
            .Should().Be("\"2013-03-21T20:04:00Z\"");
    }

    [Test]
    public void Decode_DecimalTag_ReturnsExactNumber()
    {
        Decode([0xC4, 0x82, 0x21, 0x19, 0x30, 0x39]).Should().Be("123.45");
    }

    [Test]
    public void Decode_IndefiniteDecimalTag_ReturnsExactNumber()
    {
        Decode([0xC4, 0x9F, 0x21, 0x19, 0x30, 0x39, 0xFF]).Should().Be("123.45");
    }

    [Test]
    public void Decode_UndefinedAndSimpleValues_AreExplicit()
    {
        Decode([0x82, 0xF7, 0xF8, 0x10])
            .Should().Be("""[{"$cbor":"undefined"},{"$cbor":"simple","value":16}]""");
    }

    [Test]
    public void Decode_HugeDecimalTag_PreservesComponents()
    {
        Decode([0xC4, 0x82, 0x39, 0x07, 0xCF, 0x01])
            .Should().Be("""{"tag":4,"exponent":-2000,"mantissa":1}""");
    }

    [Test]
    public void Decode_DecimalBeyondDecimalRange_PreservesComponents()
    {
        Decode([0xC4, 0x82, 0x38, 0x1D, 0x01])
            .Should().Be("""{"tag":4,"exponent":-30,"mantissa":1}""");
    }

    [Test]
    public void Decode_BigFloatBeyondDecimalRange_PreservesComponents()
    {
        Decode([0xC5, 0x82, 0x38, 0x1D, 0x01])
            .Should().Be("""{"tag":5,"exponent":-30,"mantissa":1}""");
    }

    [Test]
    public void Decode_BigFloatTag_ReturnsExactNumber()
    {
        Decode([0xC5, 0x82, 0x20, 0x03]).Should().Be("1.5");
    }

    [Test]
    public void Decode_HugeBigFloatTag_PreservesComponents()
    {
        Decode([0xC5, 0x82, 0x39, 0x07, 0xCF, 0x01])
            .Should().Be("""{"tag":5,"exponent":-2000,"mantissa":1}""");
    }

    [Test]
    public void Decode_UnknownTag_PreservesTagAndValue()
    {
        Decode([0xD8, 0x64, 0x61, 0x78]).Should().Be("""{"tag":100,"value":"x"}""");
    }

    [Test]
    public void Decode_NonFiniteFloats_ReturnsNamedStrings()
    {
        Decode([0x83, 0xF9, 0x7E, 0x00, 0xF9, 0x7C, 0x00, 0xF9, 0xFC, 0x00])
            .Should().Be("""["NaN","Infinity","-Infinity"]""");
    }

    [Test]
    public void Decode_MapWithNonTextKey_UsesLosslessEntries()
    {
        Decode([0xA1, 0x01, 0x61, 0x61])
            .Should().Be("""{"$cbor":"map","entries":[[1,"a"]]}""");
    }

    [Test]
    public void Decode_MapWithDuplicateTextKeys_UsesLosslessEntries()
    {
        Decode([0xA2, 0x61, 0x61, 0x01, 0x61, 0x61, 0x02])
            .Should().Be("""{"$cbor":"map","entries":[["a",1],["a",2]]}""");
    }

    [Test]
    public void Decode_EmptyMap_ReturnsEmptyJsonObject()
    {
        Decode([0xA0]).Should().Be("{}");
    }

    [Test]
    public void Decode_EmptyArray_ReturnsEmptyJsonArray()
    {
        Decode([0x80]).Should().Be("[]");
    }

    [Test]
    public void Decode_IndefiniteByteString_ConcatenatesAndReturnsBase64()
    {
        Decode([0x5F, 0x42, 0x01, 0x02, 0x41, 0x03, 0xFF]).Should().Be("\"AQID\"");
    }

    [Test]
    public void Decode_MapWithReservedLookingTextKey_RemainsPlainObject()
    {
        Decode([0xA2, 0x65, 0x24, 0x63, 0x62, 0x6F, 0x72, 0x01, 0x61, 0x61, 0x02])
            .Should().Be("""{"$cbor":1,"a":2}""");
    }

    [Test]
    public void Decode_MaximumSupportedNesting_ParsesAsJson()
    {
        var payload = Enumerable.Repeat((byte)0x81, 60).Append((byte)0x01).ToArray();
        var text = Decode(payload);

        using var document = System.Text.Json.JsonDocument.Parse(text);
        document.RootElement.ValueKind.Should().Be(System.Text.Json.JsonValueKind.Array);
    }

    [Test]
    public void Decode_ExcessiveNesting_ReturnsHexFallback()
    {
        var payload = Enumerable.Repeat((byte)0x81, 61).Append((byte)0x01).ToArray();
        var result = new CborPayloadDecoder().Decode(MakeArgsBytes("sensor/cbor", payload));

        result.IsFailure.Should().BeFalse();
        result.DisplayText.Should().Be(Convert.ToHexString(payload).ToLowerInvariant());
    }

    [Test]
    public void Decode_NearJsonDepthLimitMapEntries_ParsesAsJson()
    {
        var text = Decode(NestedNonTextKeyMaps(21));

        using var document = System.Text.Json.JsonDocument.Parse(text);
        document.RootElement.ValueKind.Should().Be(System.Text.Json.JsonValueKind.Object);
    }

    [Test]
    public void Decode_MapEntriesBeyondJsonDepthLimit_ReturnsHexFallback()
    {
        var payload = NestedNonTextKeyMaps(22);
        var result = new CborPayloadDecoder().Decode(MakeArgsBytes("sensor/cbor", payload));

        result.IsFailure.Should().BeFalse();
        result.DisplayText.Should().Be(Convert.ToHexString(payload).ToLowerInvariant());
    }

    [Test]
    public void Decode_DeeplyNestedTextKeyMaps_ParsesAsJson()
    {
        var text = Decode(NestedTextKeyMaps(60));

        using var document = System.Text.Json.JsonDocument.Parse(text);
        document.RootElement.ValueKind.Should().Be(System.Text.Json.JsonValueKind.Object);
    }

    [Test]
    public void Decode_MapValuesBeyondOutputLimit_ReturnsHexFallback()
    {
        var payload = MapWithEscapingTextValues(3, 300_000);
        var result = new CborPayloadDecoder().Decode(MakeArgsBytes("sensor/cbor", payload));

        result.IsFailure.Should().BeFalse();
        result.DisplayText.Should().Be(Convert.ToHexString(payload).ToLowerInvariant());
    }

    [Test]
    public void Decode_NestedMapValuesBeyondOutputLimit_ReturnsHexFallback()
    {
        var inner = MapWithEscapingTextValues(3, 300_000);
        var payload = new List<byte> { 0xA1, 0x61, 0x6E };
        payload.AddRange(inner);

        var result = new CborPayloadDecoder().Decode(MakeArgsBytes("sensor/cbor", [.. payload]));

        result.IsFailure.Should().BeFalse();
        result.DisplayText.Should().Be(Convert.ToHexString(payload.ToArray()).ToLowerInvariant());
    }

    private static byte[] MapWithEscapingTextValues(int entryCount, int valueLength)
    {
        var payload = new List<byte> { (byte)(0xA0 + entryCount) };
        for (var i = 0; i < entryCount; i++)
        {
            payload.Add(0x61);
            payload.Add((byte)('a' + i));
            payload.Add(0x7A);
            payload.Add((byte)(valueLength >> 24));
            payload.Add((byte)(valueLength >> 16));
            payload.Add((byte)(valueLength >> 8));
            payload.Add((byte)valueLength);
            payload.AddRange(new byte[valueLength]);
        }

        return [.. payload];
    }

    private static byte[] NestedNonTextKeyMaps(int depth)
    {
        var payload = new List<byte>(depth * 2 + 1);
        for (var i = 0; i < depth; i++)
        {
            payload.Add(0xA1);
            payload.Add(0x00);
        }

        payload.Add(0x01);
        return [.. payload];
    }

    private static byte[] NestedTextKeyMaps(int depth)
    {
        var payload = new List<byte>(depth * 3 + 1);
        for (var i = 0; i < depth; i++)
        {
            payload.Add(0xA1);
            payload.Add(0x61);
            payload.Add(0x61);
        }

        payload.Add(0x01);
        return [.. payload];
    }
}
