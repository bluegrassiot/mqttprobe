using MqttProbe.Models.Chart;
using MqttProbe.Models.Configuration;
using MqttProbe.Models.Mqtt;
using MqttProbe.Models.Sparkplug;
using MqttProbe.Services.Chart;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Mqtt;
using MqttProbe.Services.Platform;
using MqttProbe.Services.Security;
using MqttProbe.Services.Sparkplug;
using MqttProbe.Services.Telemetry;

namespace MqttProbe.Shared.Tests.Services.Chart;

[TestFixture]
public class ChartFieldRegistryTests
{
    private ChartFieldRegistry _registry = null!;

    [SetUp]
    public void Setup() => _registry = new ChartFieldRegistry();

    private static Dictionary<string, ExtractedField> Fields(params (string path, double value)[] entries) =>
        entries.ToDictionary(e => e.path, e => new ExtractedField(e.value));

    [Test]
    public void GetTopics_WhenEmpty_ReturnsEmpty()
    {
        _registry.GetTopics().Should().BeEmpty();
    }

    [Test]
    public void GetAllFields_WhenEmpty_ReturnsEmpty()
    {
        _registry.GetAllFields().Should().BeEmpty();
    }

    [Test]
    public void GetFields_UnknownTopic_ReturnsEmpty()
    {
        _registry.GetFields("does/not/exist").Should().BeEmpty();
    }

    [Test]
    public void Update_NewTopic_AppearsInGetTopics()
    {
        _registry.Update("sensor/temp", Fields(("temperature", 21.5)));
        _registry.GetTopics().Should().ContainSingle().Which.Should().Be("sensor/temp");
    }

    [Test]
    public void Update_NewField_ReturnedByGetFields()
    {
        _registry.Update("sensor/temp", Fields(("temperature", 21.5)));

        var fields = _registry.GetFields("sensor/temp");
        fields.Should().ContainSingle();
        fields[0].JsonPath.Should().Be("temperature");
        fields[0].LastValue.Should().BeApproximately(21.5, 0.001);
        fields[0].Topic.Should().Be("sensor/temp");
    }

    [Test]
    public void Update_ExistingField_UpdatesLastValue()
    {
        _registry.Update("t", Fields(("val", 1.0)));
        _registry.Update("t", Fields(("val", 99.0)));

        _registry.GetFields("t").Single().LastValue.Should().BeApproximately(99.0, 0.001);
    }

    [Test]
    public void Update_SameFieldTwice_ProducesOnlyOneEntry()
    {
        _registry.Update("t", Fields(("path", 1.0)));
        _registry.Update("t", Fields(("path", 2.0)));

        _registry.GetFields("t").Should().ContainSingle();
    }

    [Test]
    public void Update_MultipleFields_AllRegistered()
    {
        _registry.Update("sensor", Fields(("temp", 20.0), ("humidity", 60.0), ("pressure", 1013.0)));
        _registry.GetFields("sensor").Should().HaveCount(3);
    }

    [Test]
    public void GetTopics_ReturnsAlphabeticalOrder()
    {
        _registry.Update("zebra", Fields(("x", 1.0)));
        _registry.Update("alpha", Fields(("x", 1.0)));
        _registry.Update("mango", Fields(("x", 1.0)));

        _registry.GetTopics().Should().Equal("alpha", "mango", "zebra");
    }

    [Test]
    public void GetFields_ReturnsFieldsAlphabeticalByJsonPath()
    {
        _registry.Update("t", Fields(("z.val", 1.0), ("a.val", 2.0), ("m.val", 3.0)));

        _registry.GetFields("t").Select(f => f.JsonPath).Should().Equal("a.val", "m.val", "z.val");
    }

    [Test]
    public void GetAllFields_OrdersByTopicThenPath()
    {
        _registry.Update("beta", Fields(("x", 1.0), ("a", 2.0)));
        _registry.Update("alpha", Fields(("z", 3.0)));

        var all = _registry.GetAllFields();
        all.Should().HaveCount(3);
        all[0].Topic.Should().Be("alpha");
        all[1].Topic.Should().Be("beta");
        all[1].JsonPath.Should().Be("a");
        all[2].Topic.Should().Be("beta");
        all[2].JsonPath.Should().Be("x");
    }

    [Test]
    public void Update_MultipleTopics_AllInGetTopics()
    {
        _registry.Update("topic/a", Fields(("val", 1.0)));
        _registry.Update("topic/b", Fields(("val", 2.0)));
        _registry.Update("topic/c", Fields(("val", 3.0)));

        _registry.GetTopics().Should().HaveCount(3);
        _registry.GetAllFields().Should().HaveCount(3);
    }

    [Test]
    public void Update_UpdatesLastSeen()
    {
        var before = DateTime.UtcNow;
        _registry.Update("t", Fields(("val", 1.0)));
        var after = DateTime.UtcNow;

        var field = _registry.GetFields("t").Single();
        field.LastSeen.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }
}
