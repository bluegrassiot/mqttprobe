using System.Text.Json;
using System.Text.Json.Serialization;
using MQTTnet.Protocol;
using MqttProbe.Models.Mqtt;

namespace MqttProbe.Shared.Tests.Models.Mqtt;

[TestFixture]
public class SubscribedTopicsJsonConverterTests
{
    private static readonly JsonSerializerOptions _options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };
        return o;
    }

    private sealed class Wrapper
    {
        [JsonConverter(typeof(SubscribedTopicsJsonConverter))]
        public List<SubscribedTopic> SubscribedTopics { get; set; } = [];
    }

    [Test]
    public void Deserialize_LegacyStringArray_MapsToAtLeastOnce()
    {
        const string json = """{"subscribedTopics":["a/b","c/d"]}""";

        var wrapper = JsonSerializer.Deserialize<Wrapper>(json, _options);

        wrapper!.SubscribedTopics.Should().HaveCount(2);
        wrapper.SubscribedTopics[0].Topic.Should().Be("a/b");
        wrapper.SubscribedTopics[0].QualityOfServiceLevel.Should()
            .Be(MqttQualityOfServiceLevel.AtLeastOnce);
        wrapper.SubscribedTopics[1].Topic.Should().Be("c/d");
    }

    [Test]
    public void Deserialize_ObjectArray_PreservesQos()
    {
        const string json =
            """{"subscribedTopics":[{"topic":"x/#","qualityOfServiceLevel":"ExactlyOnce"}]}""";

        var wrapper = JsonSerializer.Deserialize<Wrapper>(json, _options);

        wrapper!.SubscribedTopics.Should().ContainSingle();
        wrapper.SubscribedTopics[0].Topic.Should().Be("x/#");
        wrapper.SubscribedTopics[0].QualityOfServiceLevel.Should()
            .Be(MqttQualityOfServiceLevel.ExactlyOnce);
    }

    [Test]
    public void Serialize_WritesObjectArray()
    {
        var wrapper = new Wrapper
        {
            SubscribedTopics =
            [
                new() { Topic = "t", QualityOfServiceLevel = MqttQualityOfServiceLevel.AtMostOnce }
            ]
        };

        var json = JsonSerializer.Serialize(wrapper, _options);

        json.Should().Contain("\"topic\":\"t\"");
        json.Should().Contain("AtMostOnce");
        json.Should().NotContain("\"subscribedTopics\":[\"t\"]");
    }

    [Test]
    public void RoundTrip_ObjectForm_PreservesEntries()
    {
        var wrapper = new Wrapper
        {
            SubscribedTopics =
            [
                new() { Topic = "a", QualityOfServiceLevel = MqttQualityOfServiceLevel.AtLeastOnce },
                new() { Topic = "b", QualityOfServiceLevel = MqttQualityOfServiceLevel.ExactlyOnce }
            ]
        };

        var json = JsonSerializer.Serialize(wrapper, _options);
        var again = JsonSerializer.Deserialize<Wrapper>(json, _options);

        again!.SubscribedTopics.Should().BeEquivalentTo(wrapper.SubscribedTopics);
    }
}
