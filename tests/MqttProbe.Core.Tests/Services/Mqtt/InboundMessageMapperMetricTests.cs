using System.Collections.Immutable;
using MQTTnet;
using MQTTnet.Protocol;
using MqttProbe.Core.Services.Mqtt;

namespace MqttProbe.Core.Tests.Services.Mqtt;

[TestFixture]
public sealed class InboundMessageMapperMetricTests
{
    [Test]
    public void Map_CopiesSparkplugMetricNames()
    {
        var source = new MqttApplicationMessageBuilder()
            .WithTopic("spBv1.0/g/NDATA/n")
            .WithPayload("raw")
            .Build();
        var names = ImmutableArray.Create("A", "A/B");

        var mapped = InboundMessageMapper.Map(source, "decoded", "spBv1.0/g/NDATA/n", null, "sparkplug-b", names);

        mapped.SparkplugMetricNames.Should().Equal("A", "A/B");
    }

    [Test]
    public void Map_WithoutMetricNames_UsesAnEmptyArray()
    {
        var source = new MqttApplicationMessageBuilder()
            .WithTopic("t")
            .WithPayload("p")
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
            .Build();

        var mapped = InboundMessageMapper.Map(source, "p", "t", null, null);

        mapped.SparkplugMetricNames.Should().BeEmpty();
    }
}
