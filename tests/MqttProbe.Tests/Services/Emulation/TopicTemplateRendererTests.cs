using MqttProbe.Models.Emulation;
using MqttProbe.Services.Emulation;

namespace MqttProbe.Shared.Tests.Services.Emulation;

[TestFixture]
public class TopicTemplateRendererTests
{
    private static EmulatorNodeConfig GenericNode(
        string template = "{group}/{node}/{device}/{metric}",
        string format = "json",
        bool withDevice = true)
    {
        var node = new EmulatorNodeConfig
        {
            Type = EmulatorNodeType.Generic,
            GroupId = "Plant1",
            NodeId = "Press-01",
            PayloadFormatId = format,
            TopicTemplate = template
        };
        if (withDevice)
            node.Devices.Add(new EmulatorDeviceConfig
            {
                DeviceId = "Sensor-1",
                Metrics = [new EmulatorMetricConfig { Name = "Flow Rate" }]
            });
        return node;
    }

    [Test]
    public void RenderMetricTopic_SubstitutesAllTokens()
    {
        var node = GenericNode();

        var topic = TopicTemplateRenderer.RenderMetricTopic(node, "Sensor-1", "Flow Rate");

        topic.Should().Be("Plant1/Press-01/Sensor-1/Flow Rate");
    }

    [Test]
    public void RenderMetricTopic_RepeatedTokens_ReplacesAllOccurrences()
    {
        var node = GenericNode(template: "{node}/{node}/{metric}");

        var topic = TopicTemplateRenderer.RenderMetricTopic(node, "Sensor-1", "Temp");

        topic.Should().Be("Press-01/Press-01/Temp");
    }

    [Test]
    public void RenderDeviceTopic_DropsMetricSegmentAndSeparator()
    {
        var node = GenericNode();

        var topic = TopicTemplateRenderer.RenderDeviceTopic(node, "Sensor-1");

        topic.Should().Be("Plant1/Press-01/Sensor-1");
    }

    [Test]
    public void RenderDeviceTopic_DropsWholeSegmentContainingMetricToken()
    {
        var node = GenericNode(template: "{group}/{node}/{device}/data-{metric}");

        var topic = TopicTemplateRenderer.RenderDeviceTopic(node, "Sensor-1");

        topic.Should().Be("Plant1/Press-01/Sensor-1");
    }

    [Test]
    public void Validate_ValidGenericJsonNode_ReturnsNoErrors()
    {
        var node = GenericNode();

        TopicTemplateRenderer.Validate(node).Should().BeEmpty();
    }

    [Test]
    public void Validate_SparkplugNode_ReturnsNoErrors()
    {
        var node = new EmulatorNodeConfig { Type = EmulatorNodeType.SparkplugB, TopicTemplate = "" };

        TopicTemplateRenderer.Validate(node).Should().BeEmpty();
    }

    [Test]
    public void Validate_TemplateWithoutNodeToken_ReturnsError()
    {
        var node = GenericNode(template: "{group}/{device}/{metric}");

        TopicTemplateRenderer.Validate(node).Should().ContainSingle(e => e.Contains("{node}"));
    }

    [Test]
    public void Validate_PlainTextTemplateWithoutMetricToken_ReturnsError()
    {
        var node = GenericNode(template: "{group}/{node}/{device}", format: "plaintext");

        TopicTemplateRenderer.Validate(node).Should().ContainSingle(e => e.Contains("{metric}"));
    }

    [Test]
    public void Validate_HexTemplateWithoutMetricToken_ReturnsError()
    {
        var node = GenericNode(template: "{group}/{node}/{device}", format: "hex");

        TopicTemplateRenderer.Validate(node).Should().ContainSingle(e => e.Contains("{metric}"));
    }

    [Test]
    public void Validate_JsonTemplateWithoutMetricToken_IsValid()
    {
        var node = GenericNode(template: "{group}/{node}/{device}", format: "json");

        TopicTemplateRenderer.Validate(node).Should().BeEmpty();
    }

    [Test]
    public void Validate_TemplateWithoutDeviceToken_WithDevices_ReturnsError()
    {
        var node = GenericNode(template: "{group}/{node}/{metric}", format: "plaintext");

        TopicTemplateRenderer.Validate(node).Should().ContainSingle(e => e.Contains("{device}"));
    }

    [Test]
    public void Validate_TemplateWithoutDeviceToken_WithoutDevices_IsValid()
    {
        var node = GenericNode(template: "{group}/{node}/{metric}", format: "plaintext", withDevice: false);

        TopicTemplateRenderer.Validate(node).Should().BeEmpty();
    }

    [TestCase("Flow/Rate")]
    [TestCase("Flow+Rate")]
    [TestCase("Flow#Rate")]
    public void Validate_MetricNameWithIllegalTopicChars_ReturnsError(string metricName)
    {
        var node = GenericNode();
        node.Devices[0].Metrics[0].Name = metricName;

        TopicTemplateRenderer.Validate(node).Should().ContainSingle(e => e.Contains(metricName));
    }

    [Test]
    public void Validate_GroupIdWithIllegalTopicChars_ReturnsError()
    {
        var node = GenericNode();
        node.GroupId = "Plant/1";

        TopicTemplateRenderer.Validate(node).Should().ContainSingle(e => e.Contains("Plant/1"));
    }

    [Test]
    public void Validate_DeviceIdWithIllegalTopicChars_ReturnsError()
    {
        var node = GenericNode();
        node.Devices[0].DeviceId = "Sensor+1";

        TopicTemplateRenderer.Validate(node).Should().ContainSingle(e => e.Contains("Sensor+1"));
    }
}
