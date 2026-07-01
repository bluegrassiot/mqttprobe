using MqttProbe.Services.Chart;

namespace MqttProbe.Shared.Tests.Services.Chart;

[TestFixture]
public class JsonFieldExtractorTests
{
    private JsonFieldExtractor _extractor = null!;

    [SetUp]
    public void Setup() => _extractor = new JsonFieldExtractor();

    [Test]
    public void Extract_FlatJson_ReturnsAllNumericFields()
    {
        var json = """{"temperature":22.4,"humidity":58,"pressure":1013.25}""";
        var result = _extractor.Extract(json);
        result.Should().HaveCount(3);
        result["temperature"].Value.Should().BeApproximately(22.4, 0.001);
        result["humidity"].Value.Should().BeApproximately(58, 0.001);
        result["pressure"].Value.Should().BeApproximately(1013.25, 0.001);
    }

    [Test]
    public void Extract_FlatJson_ContextJsonIsNull()
    {
        var json = """{"temperature":22.4,"humidity":58}""";
        var result = _extractor.Extract(json);
        result["temperature"].ContextJson.Should().BeNull();
        result["humidity"].ContextJson.Should().BeNull();
    }

    [Test]
    public void Extract_NestedObject_UsesDotNotationPaths()
    {
        var json = """{"sensors":{"indoor":{"temp":21.1,"co2":412},"outdoor":{"temp":9.8}}}""";
        var result = _extractor.Extract(json);
        result.Should().HaveCount(3);
        result["sensors.indoor.temp"].Value.Should().BeApproximately(21.1, 0.001);
        result["sensors.indoor.co2"].Value.Should().BeApproximately(412, 0.001);
        result["sensors.outdoor.temp"].Value.Should().BeApproximately(9.8, 0.001);
    }

    [Test]
    public void Extract_BooleanTrue_Returns1()
    {
        var json = """{"active":true,"standby":false}""";
        var result = _extractor.Extract(json);
        result["active"].Value.Should().Be(1.0);
        result["standby"].Value.Should().Be(0.0);
    }

    [Test]
    public void Extract_NamedValueArray_FlattensByName()
    {
        var json = """{"metrics":[{"name":"voltage","value":3.3},{"name":"current","value":0.85}]}""";
        var result = _extractor.Extract(json);
        result.Should().ContainKey("metrics.voltage");
        result["metrics.voltage"].Value.Should().BeApproximately(3.3, 0.001);
        result.Should().ContainKey("metrics.current");
        result["metrics.current"].Value.Should().BeApproximately(0.85, 0.001);
    }

    [Test]
    public void Extract_NamedValueArray_ContextJsonContainsFullElement()
    {
        var json = """{"metrics":[{"name":"voltage","value":3.3,"unit":"V","datatype":9}]}""";
        var result = _extractor.Extract(json);
        result.Should().ContainKey("metrics.voltage");
        var ctx = result["metrics.voltage"].ContextJson;
        ctx.Should().NotBeNull();
        ctx.Should().Contain("\"name\"");
        ctx.Should().Contain("voltage");
        ctx.Should().Contain("datatype");
    }

    [Test]
    public void Extract_NamedValueArray_MissingNameFallsBackToIndexedPaths()
    {
        var json = """{"metrics":[{"value":3.3}]}""";

        var result = _extractor.Extract(json);

        result.Should().ContainKey("metrics[0].value");
    }

    [TestCase("""{"metrics":[{"name":null,"value":3.3}]}""")]
    [TestCase("""{"metrics":[{"name":42,"value":3.3}]}""")]
    public void Extract_NamedValueArray_NonStringNameFallsBackToIndexedPaths(string json)
    {
        var result = _extractor.Extract(json);

        result.Should().ContainKey("metrics[0].value");
    }

    [Test]
    public void Extract_SparkplugStyleMetrics_FlattensByName()
    {
        var json = """{"metrics":[{"name":"bdSeq","value":0},{"name":"temperature","float_value":22.4}]}""";
        var result = _extractor.Extract(json);
        result.Should().ContainKey("metrics.bdSeq");
        result.Should().ContainKey("metrics.temperature");
    }

    [Test]
    public void Extract_SparkplugProtobufJsonStyle_CamelCaseValueFields()
    {
        // Google.Protobuf JSON serialises proto field names as camelCase (float_value → floatValue)
        var json = """{"metrics":[{"name":"voltage","floatValue":3.3},{"name":"bdSeq","longValue":5},{"name":"temp","doubleValue":22.4}]}""";
        var result = _extractor.Extract(json);
        result.Should().ContainKey("metrics.voltage");
        result["metrics.voltage"].Value.Should().BeApproximately(3.3, 0.001);
        result.Should().ContainKey("metrics.bdSeq");
        result["metrics.bdSeq"].Value.Should().Be(5.0);
        result.Should().ContainKey("metrics.temp");
        result["metrics.temp"].Value.Should().BeApproximately(22.4, 0.001);
    }

    [Test]
    public void Extract_SparkplugMetrics_IgnoresDatatypeOnlyMetricsAndNamesValueFieldsByMetricName()
    {
        var json = """
        {
          "timestamp": 1714050000,
          "metrics": [
            { "name": "birthMetric", "datatype": 9 },
            { "name": "Temperature", "datatype": 10, "doubleValue": 22.4 }
          ]
        }
        """;

        var result = _extractor.Extract(json);

        result.Should().ContainKey("metrics.Temperature");
        result["metrics.Temperature"].Value.Should().BeApproximately(22.4, 0.001);
        result.Should().NotContainKey("metrics[0].datatype");
        result.Should().NotContainKey("metrics[1].datatype");
        result.Should().NotContainKey("metrics[1].doubleValue");
    }

    [Test]
    public void Extract_SparkplugMetrics_ExtractsBooleanAndNumericStringValueFieldsByMetricName()
    {
        var json = """
        {
          "metrics": [
            { "name": "Metric-4", "datatype": 11, "booleanValue": true },
            { "name": "Metric-5", "datatype": 4, "longValue": "67" }
          ]
        }
        """;

        var result = _extractor.Extract(json);

        result["metrics.Metric-4"].Value.Should().Be(1.0);
        result["metrics.Metric-5"].Value.Should().Be(67.0);
        result.Should().NotContainKey("metrics[0].datatype");
        result.Should().NotContainKey("metrics[1].datatype");
    }

    [Test]
    public void Extract_IndexedArray_UsesIndexPaths()
    {
        var json = """{"readings":[10.5,20.3,30.1]}""";
        var result = _extractor.Extract(json);
        result["readings[0]"].Value.Should().BeApproximately(10.5, 0.001);
        result["readings[1]"].Value.Should().BeApproximately(20.3, 0.001);
        result["readings[2]"].Value.Should().BeApproximately(30.1, 0.001);
    }

    [Test]
    public void Extract_NonNumericStringFields_AreIgnored()
    {
        var json = """{"name":"sensor1","temperature":22.4}""";
        var result = _extractor.Extract(json);
        result.Should().HaveCount(1);
        result.Should().ContainKey("temperature");
        result.Should().NotContainKey("name");
    }

    [Test]
    public void Extract_NumericStringFields_AreExtracted()
    {
        var json = """{"temperature":"22.4","humidity":"58"}""";
        var result = _extractor.Extract(json);
        result.Should().HaveCount(2);
        result["temperature"].Value.Should().BeApproximately(22.4, 0.001);
        result["humidity"].Value.Should().BeApproximately(58, 0.001);
    }

    [TestCase("""-5.5""", -5.5)]
    [TestCase("""1.5e2""", 150.0)]
    public void Extract_NumericStringEdgeCases_AreExtracted(string numericString, double expected)
    {
        var json = $$"""{"v":"{{numericString}}"}""";
        var result = _extractor.Extract(json);
        result["v"].Value.Should().BeApproximately(expected, 0.001);
    }

    [Test]
    public void Extract_EmptyJson_ReturnsEmpty()
    {
        var result = _extractor.Extract("{}");
        result.Should().BeEmpty();
    }

    [Test]
    public void Extract_InvalidJson_ReturnsEmpty()
    {
        var result = _extractor.Extract("not json at all");
        result.Should().BeEmpty();
    }

    [Test]
    public void Extract_NullOrEmpty_ReturnsEmpty()
    {
        _extractor.Extract("").Should().BeEmpty();
        _extractor.Extract("   ").Should().BeEmpty();
    }

    [Test]
    public void Extract_MixedPayload_OnlyNumericAndBooleanLeaves()
    {
        var json = """
        {
          "timestamp": 1714050000,
          "temperature": 22.4,
          "active": true,
          "label": "sensor",
          "sensors": {
            "indoor": { "temp": 21.1 }
          }
        }
        """;
        var result = _extractor.Extract(json);
        result.Should().ContainKey("timestamp");
        result.Should().ContainKey("temperature");
        result.Should().ContainKey("active");
        result.Should().ContainKey("sensors.indoor.temp");
        result.Should().NotContainKey("label");
    }

    [Test]
    public void Extract_DeepNesting_BuildsCorrectPath()
    {
        var json = """{"a":{"b":{"c":{"d":42}}}}""";
        var result = _extractor.Extract(json);
        result.Should().ContainKey("a.b.c.d");
        result["a.b.c.d"].Value.Should().Be(42.0);
    }

    // --- Alias-aware extraction tests ---

    [Test]
    public void Extract_WithAliasMap_ResolvesAliasOnlyMetrics()
    {
        var json = """{"metrics":[{"alias":42,"doubleValue":3.14}]}""";
        var aliasNames = new Dictionary<ulong, string> { [42] = "Flow Rate" };
        var result = _extractor.Extract(json, aliasNames);

        result.Should().ContainKey("metrics.Flow Rate");
        result["metrics.Flow Rate"].Value.Should().BeApproximately(3.14, 0.001);
    }

    [Test]
    public void Extract_WithNullAliasMap_FallsBackToIndexedPaths()
    {
        var json = """{"metrics":[{"alias":42,"doubleValue":3.14}]}""";
        var result = _extractor.Extract(json, null);

        result.Should().ContainKey("metrics[0].doubleValue");
        result["metrics[0].doubleValue"].Value.Should().BeApproximately(3.14, 0.001);
    }

    [Test]
    public void Extract_WithAliasMap_AliasNotInMap_FallsBackToIndexedPaths()
    {
        var json = """{"metrics":[{"alias":99,"doubleValue":1.0}]}""";
        var aliasNames = new Dictionary<ulong, string> { [42] = "Flow Rate" };
        var result = _extractor.Extract(json, aliasNames);

        result.Should().ContainKey("metrics[0].doubleValue");
    }

    [Test]
    public void Extract_WithAliasMap_MixedNamedAndAliasOnly()
    {
        var json = """{"metrics":[{"name":"Pressure","doubleValue":1013.25},{"alias":7,"doubleValue":22.5}]}""";
        var aliasNames = new Dictionary<ulong, string> { [7] = "Temperature" };
        var result = _extractor.Extract(json, aliasNames);

        result.Should().ContainKey("metrics.Pressure");
        result.Should().ContainKey("metrics.Temperature");
        result.Should().NotContainKey("metrics[0].doubleValue");
        result.Should().NotContainKey("metrics[1].doubleValue");
    }
}
