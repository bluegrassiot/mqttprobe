using System.ComponentModel.DataAnnotations;
using MqttProbe.Models.Chart;
using MqttProbe.Models.Configuration;
using MqttProbe.Models.Mqtt;
using MqttProbe.Models.Sparkplug;

namespace MqttProbe.Shared.Tests.Models.Mqtt;

[TestFixture]
public class MqttMessageSendTests
{
    private static List<ValidationResult> Validate(MqttMessageSend model)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(model, new ValidationContext(model), results, validateAllProperties: true);
        return results;
    }

    [Test]
    public void Name_RequiredValidation_FailsWhenEmpty()
    {
        var model = new MqttMessageSend { Name = string.Empty, Payload = "value" };

        var results = Validate(model);

        results.Should().NotBeEmpty();
        results.Should().Contain(r => r.MemberNames.Contains(nameof(MqttMessageSend.Name)));
    }

    [Test]
    public void Payload_RequiredValidation_FailsWhenEmpty()
    {
        var model = new MqttMessageSend { Name = "topic/a", Payload = string.Empty };

        var results = Validate(model);

        results.Should().NotBeEmpty();
        results.Should().Contain(r => r.MemberNames.Contains(nameof(MqttMessageSend.Payload)));
    }

    [Test]
    public void StructuredPayload_PassesValidation()
    {
        var model = new MqttMessageSend { Name = "topic/a", Payload = "{\"key\":\"value\"}" };

        var results = Validate(model);

        results.Should().BeEmpty();
    }

    [Test]
    public void PlainTextPayload_PassesValidation()
    {
        var model = new MqttMessageSend { Name = "topic/a", Payload = "plain text payload" };

        var results = Validate(model);

        results.Should().BeEmpty();
    }
}
