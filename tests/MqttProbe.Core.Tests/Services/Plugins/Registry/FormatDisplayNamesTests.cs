using MqttProbe.Core.Services.Plugins.BuiltIn;
using MqttProbe.Core.Services.Plugins.Pipeline;
using MqttProbe.Core.Services.Plugins.Registry;
using MqttProbe.PluginContracts;

namespace MqttProbe.Core.Tests.Services.Plugins.Registry;

[TestFixture]
public class FormatDisplayNamesTests
{
    private static PluginRegistry BuildRegistry(params IPayloadDetector[] detectors)
    {
        var builder = new PluginRegistryBuilder();
        foreach (var d in detectors)
            builder.RegisterDetector(d);
        return builder.Build();
    }

    private static IPayloadDetector MakeDetector(string formatId, string? displayName, int priority = 100)
    {
        var detector = Substitute.For<IPayloadDetector>();
        detector.FormatId.Returns(formatId);
        detector.DisplayName.Returns(displayName);
        detector.Priority.Returns(priority);
        return detector;
    }

    private static PayloadPipeline MakePipeline(PluginRegistry registry)
    {
        var logger = Substitute.For<Microsoft.Extensions.Logging.ILogger<PayloadPipeline>>();
        return new PayloadPipeline(registry, logger);
    }

    // --- null/whitespace formatId -> null ---

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void GetDisplayName_NullOrWhitespaceFormatId_ReturnsNull(string? formatId)
    {
        var registry = BuildRegistry();
        var pipeline = MakePipeline(registry);
        var sut = new FormatDisplayNames(pipeline);

        sut.GetDisplayName(formatId).Should().BeNull();
    }

    // --- Known detector with DisplayName -> returns name ---

    [Test]
    public void GetDisplayName_KnownDetectorWithDisplayName_ReturnsName()
    {
        var detector = MakeDetector("json", "JSON");
        var registry = BuildRegistry(detector);
        var pipeline = MakePipeline(registry);
        var sut = new FormatDisplayNames(pipeline);

        sut.GetDisplayName("json").Should().Be("JSON");
    }

    [Test]
    public void GetDisplayName_KnownDetectorWithWhitespaceDisplayName_ReturnsRawId()
    {
        var detector = MakeDetector("custom", "   ");
        var registry = BuildRegistry(detector);
        var pipeline = MakePipeline(registry);
        var sut = new FormatDisplayNames(pipeline);

        sut.GetDisplayName("custom").Should().Be("custom");
    }

    [Test]
    public void GetDisplayName_KnownDetectorWithNullDisplayName_ReturnsRawId()
    {
        var detector = MakeDetector("custom", null);
        var registry = BuildRegistry(detector);
        var pipeline = MakePipeline(registry);
        var sut = new FormatDisplayNames(pipeline);

        sut.GetDisplayName("custom").Should().Be("custom");
    }

    // --- Unknown formatId -> returns raw id ---

    [Test]
    public void GetDisplayName_UnknownFormatId_ReturnsRawId()
    {
        var registry = BuildRegistry();
        var pipeline = MakePipeline(registry);
        var sut = new FormatDisplayNames(pipeline);

        sut.GetDisplayName("unknown-format").Should().Be("unknown-format");
    }

    // --- DisplayName trimmed ---

    [Test]
    public void GetDisplayName_DisplayNameWithLeadingTrailingWhitespace_IsTrimmed()
    {
        var detector = MakeDetector("csv", "  CSV  ");
        var registry = BuildRegistry(detector);
        var pipeline = MakePipeline(registry);
        var sut = new FormatDisplayNames(pipeline);

        sut.GetDisplayName("csv").Should().Be("CSV");
    }

    // --- After SwapRegistry, lookup updates ---

    [Test]
    public void GetDisplayName_AfterSwapRegistry_ReflectsNewDetectors()
    {
        var detectorV1 = MakeDetector("json", "JSON v1");
        var registryV1 = BuildRegistry(detectorV1);
        var pipeline = MakePipeline(registryV1);
        var sut = new FormatDisplayNames(pipeline);

        sut.GetDisplayName("json").Should().Be("JSON v1");

        var detectorV2 = MakeDetector("json", "JSON v2");
        var registryV2 = BuildRegistry(detectorV2);
        pipeline.SwapRegistry(registryV2);

        sut.GetDisplayName("json").Should().Be("JSON v2");
    }

    [Test]
    public void GetDisplayName_AfterSwapRegistry_RemovedDetectorFallsBackToRawId()
    {
        var detector = MakeDetector("custom", "Custom");
        var registryV1 = BuildRegistry(detector);
        var pipeline = MakePipeline(registryV1);
        var sut = new FormatDisplayNames(pipeline);

        sut.GetDisplayName("custom").Should().Be("Custom");

        var registryV2 = BuildRegistry();
        pipeline.SwapRegistry(registryV2);

        sut.GetDisplayName("custom").Should().Be("custom");
    }

    // --- FormatId keys are case-sensitive (Ordinal) ---

    [Test]
    public void GetDisplayName_FormatIdIsCaseSensitive()
    {
        var detector = MakeDetector("json", "JSON");
        var registry = BuildRegistry(detector);
        var pipeline = MakePipeline(registry);
        var sut = new FormatDisplayNames(pipeline);

        sut.GetDisplayName("JSON").Should().Be("JSON", "uppercase is unknown, falls back to raw id");
        sut.GetDisplayName("json").Should().Be("JSON", "lowercase matches");
    }

    // --- Built-in detectors have correct display names ---

    [Test]
    public void GetDisplayName_AllBuiltInDetectors_ReturnExpectedNames()
    {
        var expected = new Dictionary<string, string>
        {
            ["empty"] = "Empty",
            ["sparkplug-b"] = "Sparkplug B",
            ["messagepack"] = "MessagePack",
            ["binary"] = "Binary",
            ["json"] = "JSON",
            ["xml"] = "XML",
            ["hex"] = "Hex text",
            ["base64"] = "Base64 text",
            ["plaintext"] = "Plain text",
        };

        var builder = new PluginRegistryBuilder();
        BuiltInPluginRegistration.RegisterBuiltIns(builder);
        var registry = builder.Build();
        var pipeline = MakePipeline(registry);
        var sut = new FormatDisplayNames(pipeline);

        foreach (var (formatId, displayName) in expected)
        {
            sut.GetDisplayName(formatId).Should().Be(displayName, because: $"FormatId '{formatId}' should have DisplayName '{displayName}'");
        }
    }

    [Test]
    public void GetDisplayName_ProtobufDetector_ReturnsExpectedName()
    {
        // Protobuf is conditionally registered (only when schemas are found),
        // so we test it separately with a manual detector.
        var detector = MakeDetector("protobuf", "Protobuf");
        var registry = BuildRegistry(detector);
        var pipeline = MakePipeline(registry);
        var sut = new FormatDisplayNames(pipeline);

        sut.GetDisplayName("protobuf").Should().Be("Protobuf");
    }
}
