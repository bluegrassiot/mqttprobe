using System.Text.Json;
using System.Text.Json.Serialization;
using MqttProbe.Core.Models.Mqtt;

namespace MqttProbe.Core.Tests.Models.Mqtt;

[TestFixture]
public sealed class ConnectionTypedExcludeTests
{
    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    [Test]
    public void Clone_DeepCopiesSparkplugExcludes()
    {
        var original = new Connection
        {
            SparkplugTopicExcludes =
            [
                new SparkplugTopicExclude(
                    new SparkplugSelectionScope(SparkplugSelectionKind.Node, "spBv1.0/plant1", "node1"))
            ]
        };

        var clone = original.Clone();
        clone.SparkplugTopicExcludes.Add(new SparkplugTopicExclude(
            new SparkplugSelectionScope(SparkplugSelectionKind.Device, "spBv1.0/plant1", "node1", "dev1")));

        original.SparkplugTopicExcludes.Should().ContainSingle();
        clone.SparkplugTopicExcludes.Should().HaveCount(2);
    }

    [Test]
    public void Equals_ConsidersSparkplugExcludes()
    {
        var a = new Connection
        {
            ClientId = "same",
            SparkplugTopicExcludes =
            [
                new SparkplugTopicExclude(
                    new SparkplugSelectionScope(SparkplugSelectionKind.Node, "spBv1.0/plant1", "node1"))
            ]
        };
        var b = a.Clone();
        b.SparkplugTopicExcludes =
        [
            new SparkplugTopicExclude(
                new SparkplugSelectionScope(SparkplugSelectionKind.Node, "spBv1.0/plant1", "node2"))
        ];

        a.Equals(b).Should().BeFalse();
        a.GetHashCode().Should().NotBe(b.GetHashCode());
    }

    [Test]
    public void JsonRoundTrip_PreservesSparkplugExcludes()
    {
        var original = new Connection
        {
            TopicExcludes = ["sensors/temp/#"],
            SparkplugTopicExcludes =
            [
                new SparkplugTopicExclude(
                    new SparkplugSelectionScope(SparkplugSelectionKind.Node, "spBv1.0/plant1", "node1")),
                new SparkplugTopicExclude(
                    new SparkplugSelectionScope(SparkplugSelectionKind.Device, "spBv1.0/plant1", "node1", "dev1"),
                    "DDATA")
            ]
        };

        var json = JsonSerializer.Serialize(original, _options);
        var restored = JsonSerializer.Deserialize<Connection>(json, _options);

        restored.Should().NotBeNull();
        restored.TopicExcludes.Should().Equal("sensors/temp/#");
        restored.SparkplugTopicExcludes.Should().Equal(original.SparkplugTopicExcludes);
    }

    [Test]
    public void JsonWithoutSparkplugExcludes_LoadsAnEmptyTypedList()
    {
        const string json = """{"clientId":"old","topicExcludes":["sensors/temp/#"]}""";

        var restored = JsonSerializer.Deserialize<Connection>(json, _options);

        restored.Should().NotBeNull();
        restored.TopicExcludes.Should().Equal("sensors/temp/#");
        restored.SparkplugTopicExcludes.Should().BeEmpty();
    }
}
