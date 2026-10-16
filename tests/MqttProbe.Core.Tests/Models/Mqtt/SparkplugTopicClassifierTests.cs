using MqttProbe.Core.Models.Mqtt;

namespace MqttProbe.Core.Tests.Models.Mqtt;

[TestFixture]
public sealed class SparkplugTopicClassifierTests
{
    [TestCase("NBIRTH", true, false)]
    [TestCase("NDEATH", true, false)]
    [TestCase("NDATA", true, false)]
    [TestCase("NCMD", true, false)]
    [TestCase("DBIRTH", false, true)]
    [TestCase("DDEATH", false, true)]
    [TestCase("DDATA", false, true)]
    [TestCase("DCMD", false, true)]
    [TestCase("STATE", false, false)]
    [TestCase("ncmd", false, false)]
    [TestCase("NDATA1", false, false)]
    [TestCase("", false, false)]
    public void VerbClassification_MatchesTheSupportedSparkplugVerbs(
        string verb, bool isNodeVerb, bool isDeviceVerb)
    {
        SparkplugTopicClassifier.IsNodeVerb(verb).Should().Be(isNodeVerb);
        SparkplugTopicClassifier.IsDeviceVerb(verb).Should().Be(isDeviceVerb);
    }

    [Test]
    public void Namespace_IsTheSparkplugNamespace() =>
        SparkplugTopicClassifier.Namespace.Should().Be("spBv1.0");
}
