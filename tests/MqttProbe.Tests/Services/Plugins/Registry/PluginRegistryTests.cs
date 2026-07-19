using System.Text;
using MQTTnet;
using MqttProbe.Services.Plugins;
using MqttProbe.Services.Plugins.Contracts;
using MqttProbe.Services.Plugins.Registry;

namespace MqttProbe.Shared.Tests.Services.Plugins.Registry;

[TestFixture]
public class PluginRegistryTests
{
    private static MqttApplicationMessageReceivedEventArgs MakeArgs(string topic, byte[] payload)
    {
        var appMsg = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .Build();
        var packet = new MQTTnet.Packets.MqttPublishPacket { Topic = topic };
        return new MqttApplicationMessageReceivedEventArgs("test-client", appMsg, packet, null);
    }

    private static MqttApplicationMessageReceivedEventArgs MakeArgs(string topic, string payload)
        => MakeArgs(topic, Encoding.UTF8.GetBytes(payload));

    private static IPayloadDetector MakeDetector(string formatId, int priority, bool canDetect = false)
    {
        var detector = Substitute.For<IPayloadDetector>();
        detector.FormatId.Returns(formatId);
        detector.Priority.Returns(priority);
        detector.CanDetect(Arg.Any<MqttApplicationMessageReceivedEventArgs>()).Returns(canDetect);
        return detector;
    }

    private static IPayloadDecoder MakeDecoder(string formatId)
    {
        var decoder = Substitute.For<IPayloadDecoder>();
        decoder.FormatId.Returns(formatId);
        return decoder;
    }

    private static ITopologyExtractor MakeTopologyExtractor(string formatId)
    {
        var extractor = Substitute.For<ITopologyExtractor>();
        extractor.FormatId.Returns(formatId);
        return extractor;
    }

    private static IPayloadEncoder MakeEncoder(string formatId)
    {
        var encoder = Substitute.For<IPayloadEncoder>();
        encoder.FormatId.Returns(formatId);
        return encoder;
    }

    private static IPayloadTemplateProvider MakeTemplateProvider(string formatId)
    {
        var provider = Substitute.For<IPayloadTemplateProvider>();
        provider.FormatId.Returns(formatId);
        return provider;
    }

    // --- Detector ordering ---

    [Test]
    public void Detectors_OrderedByPriorityDescending()
    {
        var builder = new PluginRegistryBuilder();
        var low = MakeDetector("low", 50);
        var mid = MakeDetector("mid", 100);
        var high = MakeDetector("high", 200);

        builder.RegisterDetector(low);
        builder.RegisterDetector(mid);
        builder.RegisterDetector(high);

        var registry = builder.Build();

        registry.Detectors.Should().HaveCount(3);
        registry.Detectors[0].Should().BeSameAs(high);
        registry.Detectors[1].Should().BeSameAs(mid);
        registry.Detectors[2].Should().BeSameAs(low);
    }

    [Test]
    public void Detectors_SamePriority_PreservesRegistrationOrder()
    {
        var builder = new PluginRegistryBuilder();
        var first = MakeDetector("first", 100);
        var second = MakeDetector("second", 100);
        var third = MakeDetector("third", 100);

        builder.RegisterDetector(first);
        builder.RegisterDetector(second);
        builder.RegisterDetector(third);

        var registry = builder.Build();

        registry.Detectors[0].Should().BeSameAs(first);
        registry.Detectors[1].Should().BeSameAs(second);
        registry.Detectors[2].Should().BeSameAs(third);
    }

    // --- FindDetector ---

    [Test]
    public void FindDetector_ReturnsFirstMatchingDetector()
    {
        var builder = new PluginRegistryBuilder();
        var noMatch = MakeDetector("no", 200, canDetect: false);
        var match = MakeDetector("yes", 100, canDetect: true);

        builder.RegisterDetector(noMatch);
        builder.RegisterDetector(match);

        var registry = builder.Build();
        var result = registry.FindDetector(MakeArgs("t", "v"));

        result.Should().BeSameAs(match);
    }

    [Test]
    public void FindDetector_ReturnsNullWhenNoneMatch()
    {
        var builder = new PluginRegistryBuilder();
        builder.RegisterDetector(MakeDetector("a", 100, canDetect: false));

        var registry = builder.Build();
        var result = registry.FindDetector(MakeArgs("t", "v"));

        result.Should().BeNull();
    }

    // --- Decoders ---

    [Test]
    public void FindDecoder_ReturnsDecoderByFormatId()
    {
        var builder = new PluginRegistryBuilder();
        var decoder = MakeDecoder("json");

        builder.RegisterDecoder(decoder);

        var registry = builder.Build();
        registry.FindDecoder("json").Should().BeSameAs(decoder);
    }

    [Test]
    public void FindDecoder_ReturnsNullForUnknownFormatId()
    {
        var builder = new PluginRegistryBuilder();
        var registry = builder.Build();

        registry.FindDecoder("unknown").Should().BeNull();
    }

    [Test]
    public void Decoders_DictionaryKeyedFormatId()
    {
        var builder = new PluginRegistryBuilder();
        builder.RegisterDecoder(MakeDecoder("json"));
        builder.RegisterDecoder(MakeDecoder("xml"));

        var registry = builder.Build();

        registry.Decoders.Should().HaveCount(2);
        registry.Decoders.Should().ContainKey("json");
        registry.Decoders.Should().ContainKey("xml");
    }

    // --- Encoders ---

    [Test]
    public void FindEncoder_ReturnsEncoderByFormatId()
    {
        var builder = new PluginRegistryBuilder();
        var encoder = MakeEncoder("json");

        builder.RegisterEncoder(encoder);

        var registry = builder.Build();
        registry.FindEncoder("json").Should().BeSameAs(encoder);
    }

    [Test]
    public void FindEncoder_ReturnsNullForUnknownFormatId()
    {
        var builder = new PluginRegistryBuilder();
        var registry = builder.Build();

        registry.FindEncoder("unknown").Should().BeNull();
    }

    // --- Topology Extractors (single per FormatId) ---

    [Test]
    public void FindTopologyExtractor_ReturnsExtractorByFormatId()
    {
        var builder = new PluginRegistryBuilder();
        var ext = MakeTopologyExtractor("sparkplug-b");

        builder.RegisterTopologyExtractor(ext);

        var registry = builder.Build();
        var result = registry.FindTopologyExtractor("sparkplug-b");

        result.Should().BeSameAs(ext);
    }

    [Test]
    public void FindTopologyExtractor_ReturnsNullForUnknownFormatId()
    {
        var builder = new PluginRegistryBuilder();
        var registry = builder.Build();

        registry.FindTopologyExtractor("unknown").Should().BeNull();
    }

    [Test]
    public void TopologyExtractors_SinglePerFormatId()
    {
        var builder = new PluginRegistryBuilder();
        builder.RegisterTopologyExtractor(MakeTopologyExtractor("a"));
        builder.RegisterTopologyExtractor(MakeTopologyExtractor("b"));

        var registry = builder.Build();

        registry.TopologyExtractors.Should().HaveCount(2);
        registry.TopologyExtractors.Should().ContainKey("a");
        registry.TopologyExtractors.Should().ContainKey("b");
    }

    // --- Template Providers ---

    [Test]
    public void TemplateProviders_KeyedByFormatId()
    {
        var builder = new PluginRegistryBuilder();
        builder.RegisterTemplateProvider(MakeTemplateProvider("json"));

        var registry = builder.Build();

        registry.TemplateProviders.Should().HaveCount(1);
        registry.TemplateProviders.Should().ContainKey("json");
    }

    // --- Diagnostics: duplicate plugin IDs ---

    [Test]
    public void DuplicatePluginId_RecordsErrorDiagnostic()
    {
        var builder = new PluginRegistryBuilder();

        builder.SetCurrentPluginId("plugin-a");
        builder.RegisterDetector(MakeDetector("json", 100));

        builder.SetCurrentPluginId("plugin-a");
        builder.RegisterDecoder(MakeDecoder("json"));

        var registry = builder.Build();

        registry.Diagnostics.Should().Contain(d =>
            d.Source == "plugin-a" &&
            d.Severity == DiagnosticSeverity.Error &&
            d.Message.ToLowerInvariant().Contains("duplicate"));
    }

    [Test]
    public void DuplicatePluginId_SecondRegistrationIsIgnored()
    {
        var builder = new PluginRegistryBuilder();

        builder.SetCurrentPluginId("plugin-a");
        var firstDecoder = MakeDecoder("json");
        builder.RegisterDecoder(firstDecoder);

        builder.SetCurrentPluginId("plugin-a");
        var secondDecoder = MakeDecoder("xml");
        builder.RegisterDecoder(secondDecoder);

        var registry = builder.Build();

        registry.Decoders.Should().HaveCount(1);
        registry.Decoders["json"].Should().BeSameAs(firstDecoder);
    }

    // --- Diagnostics: duplicate FormatId per capability ---

    [Test]
    public void DuplicateDecoderFormatId_SecondDisabledWithWarning()
    {
        var builder = new PluginRegistryBuilder();

        builder.SetCurrentPluginId("plugin-a");
        var first = MakeDecoder("json");
        builder.RegisterDecoder(first);

        builder.SetCurrentPluginId("plugin-b");
        var second = MakeDecoder("json");
        builder.RegisterDecoder(second);

        var registry = builder.Build();

        registry.Decoders.Should().HaveCount(1);
        registry.Decoders["json"].Should().BeSameAs(first);

        registry.Diagnostics.Should().Contain(d =>
            d.Source == "plugin-b" &&
            d.Severity == DiagnosticSeverity.Warning &&
            d.Message.ToLowerInvariant().Contains("disabled"));
    }

    [Test]
    public void DuplicateEncoderFormatId_SecondDisabledWithWarning()
    {
        var builder = new PluginRegistryBuilder();

        builder.SetCurrentPluginId("plugin-a");
        var first = MakeEncoder("json");
        builder.RegisterEncoder(first);

        builder.SetCurrentPluginId("plugin-b");
        var second = MakeEncoder("json");
        builder.RegisterEncoder(second);

        var registry = builder.Build();

        registry.Encoders.Should().HaveCount(1);
        registry.Encoders["json"].Should().BeSameAs(first);
    }

    // --- Disable ---

    [Test]
    public void DisabledPluginId_AllRegistrationsPrevented()
    {
        var builder = new PluginRegistryBuilder();

        builder.SetCurrentPluginId("plugin-a");
        builder.RegisterDetector(MakeDetector("json", 100));
        builder.RegisterDecoder(MakeDecoder("json"));
        builder.RegisterEncoder(MakeEncoder("json"));

        var registry = builder.Build(disabledPluginIds: ["plugin-a"]);

        registry.Detectors.Should().BeEmpty();
        registry.Decoders.Should().BeEmpty();
        registry.Encoders.Should().BeEmpty();

        registry.Diagnostics.Should().Contain(d =>
            d.Source == "plugin-a" &&
            d.Severity == DiagnosticSeverity.Info &&
            d.Message.ToLowerInvariant().Contains("disabled"));
    }

    [Test]
    public void DisabledPluginId_OtherPluginsUnaffected()
    {
        var builder = new PluginRegistryBuilder();

        builder.SetCurrentPluginId("plugin-a");
        var detectorA = MakeDetector("json", 100);
        builder.RegisterDetector(detectorA);

        builder.SetCurrentPluginId("plugin-b");
        var detectorB = MakeDetector("xml", 50);
        builder.RegisterDetector(detectorB);

        var registry = builder.Build(disabledPluginIds: ["plugin-a"]);

        registry.Detectors.Should().HaveCount(1);
        registry.Detectors[0].Should().BeSameAs(detectorB);
    }

    // --- Override (target present) ---

    [Test]
    public void Override_ReplacesBuiltIn()
    {
        var builder = new PluginRegistryBuilder();

        builder.SetCurrentPluginId(PluginRegistryBuilder.BuiltInPluginId);
        var builtIn = MakeDecoder("json");
        builder.RegisterDecoder(builtIn);

        builder.SetCurrentPluginId("external-plugin");
        var external = MakeDecoder("json");
        builder.RegisterDecoder(external);

        var registry = builder.Build(
            overrides: [new PluginOverrideConfig
            {
                FormatId = "json",
                Capability = "Decoder",
                PluginId = "external-plugin"
            }]);

        registry.Decoders["json"].Should().BeSameAs(external);
    }

    [Test]
    public void Override_DisplacedBuiltInRecordsDiagnostic()
    {
        var builder = new PluginRegistryBuilder();

        builder.SetCurrentPluginId(PluginRegistryBuilder.BuiltInPluginId);
        builder.RegisterDecoder(MakeDecoder("json"));

        builder.SetCurrentPluginId("external-plugin");
        builder.RegisterDecoder(MakeDecoder("json"));

        var registry = builder.Build(
            overrides: [new PluginOverrideConfig
            {
                FormatId = "json",
                Capability = "Decoder",
                PluginId = "external-plugin"
            }]);

        var overrideDiagnostic = registry.Diagnostics.FirstOrDefault(d =>
            d.Source == PluginRegistryBuilder.BuiltInPluginId &&
            d.Severity == DiagnosticSeverity.Info);

        overrideDiagnostic.Should().NotBeNull();
        overrideDiagnostic!.Message.ToLowerInvariant().Should().Contain("overridden");
    }

    // --- Override (target absent) ---

    [Test]
    public void Override_TargetAbsent_BuiltInStillWinsOverExternal()
    {
        var builder = new PluginRegistryBuilder();

        builder.SetCurrentPluginId("external-plugin");
        var external = MakeDecoder("json");
        builder.RegisterDecoder(external);

        builder.SetCurrentPluginId(PluginRegistryBuilder.BuiltInPluginId);
        var builtIn = MakeDecoder("json");
        builder.RegisterDecoder(builtIn);

        var registry = builder.Build(
            overrides: [new PluginOverrideConfig
            {
                FormatId = "json",
                Capability = "Decoder",
                PluginId = "nonexistent-plugin"
            }]);

        registry.Decoders["json"].Should().BeSameAs(builtIn);
    }

    [Test]
    public void Override_TargetAbsent_RecordsWarningDiagnostic()
    {
        var builder = new PluginRegistryBuilder();

        builder.SetCurrentPluginId("external-plugin");
        builder.RegisterDecoder(MakeDecoder("json"));

        builder.SetCurrentPluginId(PluginRegistryBuilder.BuiltInPluginId);
        builder.RegisterDecoder(MakeDecoder("json"));

        var registry = builder.Build(
            overrides: [new PluginOverrideConfig
            {
                FormatId = "json",
                Capability = "Decoder",
                PluginId = "nonexistent-plugin"
            }]);

        registry.Diagnostics.Should().Contain(d =>
            d.Severity == DiagnosticSeverity.Warning &&
            d.Message.Contains("nonexistent-plugin") &&
            d.Message.ToLowerInvariant().Contains("did not register"));
    }

    [Test]
    public void Override_TargetAbsent_EarlierExternalWinsOverLaterExternal()
    {
        var builder = new PluginRegistryBuilder();

        builder.SetCurrentPluginId("plugin-a");
        var first = MakeEncoder("json");
        builder.RegisterEncoder(first);

        builder.SetCurrentPluginId("plugin-b");
        var second = MakeEncoder("json");
        builder.RegisterEncoder(second);

        var registry = builder.Build(
            overrides: [new PluginOverrideConfig
            {
                FormatId = "json",
                Capability = "Encoder",
                PluginId = "nonexistent-plugin"
            }]);

        registry.Encoders["json"].Should().BeSameAs(first);
    }

    // --- Override (target disabled) ---

    [Test]
    public void Override_TargetDisabled_BuiltInStillWins()
    {
        var builder = new PluginRegistryBuilder();

        builder.SetCurrentPluginId("override-target");
        var target = MakeDecoder("json");
        builder.RegisterDecoder(target);

        builder.SetCurrentPluginId(PluginRegistryBuilder.BuiltInPluginId);
        var builtIn = MakeDecoder("json");
        builder.RegisterDecoder(builtIn);

        var registry = builder.Build(
            disabledPluginIds: ["override-target"],
            overrides: [new PluginOverrideConfig
            {
                FormatId = "json",
                Capability = "Decoder",
                PluginId = "override-target"
            }]);

        registry.Decoders["json"].Should().BeSameAs(builtIn);
    }

    [Test]
    public void Override_TargetDisabled_RecordsWarningDiagnostic()
    {
        var builder = new PluginRegistryBuilder();

        builder.SetCurrentPluginId("override-target");
        builder.RegisterDecoder(MakeDecoder("json"));

        builder.SetCurrentPluginId(PluginRegistryBuilder.BuiltInPluginId);
        builder.RegisterDecoder(MakeDecoder("json"));

        var registry = builder.Build(
            disabledPluginIds: ["override-target"],
            overrides: [new PluginOverrideConfig
            {
                FormatId = "json",
                Capability = "Decoder",
                PluginId = "override-target"
            }]);

        registry.Diagnostics.Should().Contain(d =>
            d.Severity == DiagnosticSeverity.Warning &&
            d.Message.Contains("override-target") &&
            d.Message.ToLowerInvariant().Contains("is disabled"));
    }

    // --- Built-in precedence ---

    [Test]
    public void BuiltInDecoder_AlwaysWinsOverExternal_WithoutOverride()
    {
        var builder = new PluginRegistryBuilder();

        builder.SetCurrentPluginId("external-plugin");
        var external = MakeDecoder("json");
        builder.RegisterDecoder(external);

        builder.SetCurrentPluginId(PluginRegistryBuilder.BuiltInPluginId);
        var builtIn = MakeDecoder("json");
        builder.RegisterDecoder(builtIn);

        var registry = builder.Build();

        registry.Decoders["json"].Should().BeSameAs(builtIn);
    }

    [Test]
    public void BuiltInEncoder_AlwaysWinsOverExternal_WithoutOverride()
    {
        var builder = new PluginRegistryBuilder();

        builder.SetCurrentPluginId("external-plugin");
        var external = MakeEncoder("json");
        builder.RegisterEncoder(external);

        builder.SetCurrentPluginId(PluginRegistryBuilder.BuiltInPluginId);
        var builtIn = MakeEncoder("json");
        builder.RegisterEncoder(builtIn);

        var registry = builder.Build();

        registry.Encoders["json"].Should().BeSameAs(builtIn);
    }

    [Test]
    public void BuiltInDetector_AlwaysWinsOverExternal_WithoutOverride()
    {
        var builder = new PluginRegistryBuilder();

        builder.SetCurrentPluginId("external-plugin");
        var external = MakeDetector("json", 200);
        builder.RegisterDetector(external);

        builder.SetCurrentPluginId(PluginRegistryBuilder.BuiltInPluginId);
        var builtIn = MakeDetector("json", 50);
        builder.RegisterDetector(builtIn);

        var registry = builder.Build();

        registry.Detectors.Should().ContainSingle().Which.Should().BeSameAs(builtIn);
    }

    [Test]
    public void BuiltInTopologyExtractor_AlwaysWinsOverExternal_WithoutOverride()
    {
        var builder = new PluginRegistryBuilder();

        builder.SetCurrentPluginId("external-plugin");
        var external = MakeTopologyExtractor("sparkplug-b");
        builder.RegisterTopologyExtractor(external);

        builder.SetCurrentPluginId(PluginRegistryBuilder.BuiltInPluginId);
        var builtIn = MakeTopologyExtractor("sparkplug-b");
        builder.RegisterTopologyExtractor(builtIn);

        var registry = builder.Build();

        registry.TopologyExtractors["sparkplug-b"].Should().BeSameAs(builtIn);
    }

    [Test]
    public void BuiltInTemplateProvider_AlwaysWinsOverExternal_WithoutOverride()
    {
        var builder = new PluginRegistryBuilder();

        builder.SetCurrentPluginId("external-plugin");
        var external = MakeTemplateProvider("json");
        builder.RegisterTemplateProvider(external);

        builder.SetCurrentPluginId(PluginRegistryBuilder.BuiltInPluginId);
        var builtIn = MakeTemplateProvider("json");
        builder.RegisterTemplateProvider(builtIn);

        var registry = builder.Build();

        registry.TemplateProviders["json"].Should().BeSameAs(builtIn);
    }

    // --- Detector conflict diagnostics ---

    [Test]
    public void DuplicateDetectorFormatId_BuiltInWins_ExternalDisabledWithWarning()
    {
        var builder = new PluginRegistryBuilder();

        builder.SetCurrentPluginId(PluginRegistryBuilder.BuiltInPluginId);
        var builtIn = MakeDetector("json", 50);
        builder.RegisterDetector(builtIn);

        builder.SetCurrentPluginId("external-plugin");
        var external = MakeDetector("json", 200);
        builder.RegisterDetector(external);

        var registry = builder.Build();

        registry.Detectors.Should().ContainSingle().Which.Should().BeSameAs(builtIn);

        registry.Diagnostics.Should().Contain(d =>
            d.Source == "external-plugin" &&
            d.Severity == DiagnosticSeverity.Warning &&
            d.Message.ToLowerInvariant().Contains("disabled"));
    }

    [Test]
    public void DuplicateDetectorFormatId_OverrideReplacesBuiltIn()
    {
        var builder = new PluginRegistryBuilder();

        builder.SetCurrentPluginId(PluginRegistryBuilder.BuiltInPluginId);
        var builtIn = MakeDetector("json", 50);
        builder.RegisterDetector(builtIn);

        builder.SetCurrentPluginId("external-plugin");
        var external = MakeDetector("json", 200);
        builder.RegisterDetector(external);

        var registry = builder.Build(
            overrides: [new PluginOverrideConfig
            {
                FormatId = "json",
                Capability = "Detector",
                PluginId = "external-plugin"
            }]);

        registry.Detectors.Should().ContainSingle().Which.Should().BeSameAs(external);

        registry.Diagnostics.Should().Contain(d =>
            d.Source == PluginRegistryBuilder.BuiltInPluginId &&
            d.Severity == DiagnosticSeverity.Info &&
            d.Message.ToLowerInvariant().Contains("overridden"));
    }

    // --- Topology extractor conflict diagnostics ---

    [Test]
    public void DuplicateTopologyExtractorFormatId_BuiltInWins_ExternalDisabledWithWarning()
    {
        var builder = new PluginRegistryBuilder();

        builder.SetCurrentPluginId(PluginRegistryBuilder.BuiltInPluginId);
        var builtIn = MakeTopologyExtractor("sparkplug-b");
        builder.RegisterTopologyExtractor(builtIn);

        builder.SetCurrentPluginId("external-plugin");
        var external = MakeTopologyExtractor("sparkplug-b");
        builder.RegisterTopologyExtractor(external);

        var registry = builder.Build();

        registry.TopologyExtractors["sparkplug-b"].Should().BeSameAs(builtIn);

        registry.Diagnostics.Should().Contain(d =>
            d.Source == "external-plugin" &&
            d.Severity == DiagnosticSeverity.Warning &&
            d.Message.ToLowerInvariant().Contains("disabled"));
    }

    [Test]
    public void DuplicateTopologyExtractorFormatId_OverrideReplacesBuiltIn()
    {
        var builder = new PluginRegistryBuilder();

        builder.SetCurrentPluginId(PluginRegistryBuilder.BuiltInPluginId);
        var builtIn = MakeTopologyExtractor("sparkplug-b");
        builder.RegisterTopologyExtractor(builtIn);

        builder.SetCurrentPluginId("external-plugin");
        var external = MakeTopologyExtractor("sparkplug-b");
        builder.RegisterTopologyExtractor(external);

        var registry = builder.Build(
            overrides: [new PluginOverrideConfig
            {
                FormatId = "sparkplug-b",
                Capability = "TopologyExtractor",
                PluginId = "external-plugin"
            }]);

        registry.TopologyExtractors["sparkplug-b"].Should().BeSameAs(external);
    }

    // --- Template provider conflict diagnostics ---

    [Test]
    public void DuplicateTemplateProviderFormatId_SecondDisabledWithWarning()
    {
        var builder = new PluginRegistryBuilder();

        builder.SetCurrentPluginId("plugin-a");
        var first = MakeTemplateProvider("json");
        builder.RegisterTemplateProvider(first);

        builder.SetCurrentPluginId("plugin-b");
        var second = MakeTemplateProvider("json");
        builder.RegisterTemplateProvider(second);

        var registry = builder.Build();

        registry.TemplateProviders.Should().HaveCount(1);
        registry.TemplateProviders["json"].Should().BeSameAs(first);

        registry.Diagnostics.Should().Contain(d =>
            d.Source == "plugin-b" &&
            d.Severity == DiagnosticSeverity.Warning &&
            d.Message.ToLowerInvariant().Contains("disabled"));
    }

    // --- RegisterPlugin scoped API ---

    [Test]
    public void RegisterPlugin_ScopedRegistration()
    {
        var builder = new PluginRegistryBuilder();

        builder.RegisterPlugin("plugin-a", ctx =>
        {
            ctx.RegisterDetector(MakeDetector("json", 100));
            ctx.RegisterDecoder(MakeDecoder("json"));
        });

        var registry = builder.Build();

        registry.Detectors.Should().HaveCount(1);
        registry.Decoders.Should().HaveCount(1);
        registry.Decoders.Should().ContainKey("json");
    }

    [Test]
    public void RegisterPlugin_DuplicateId_RecordsErrorAndSkips()
    {
        var builder = new PluginRegistryBuilder();

        builder.RegisterPlugin("plugin-a", ctx =>
        {
            ctx.RegisterDecoder(MakeDecoder("json"));
        });

        builder.RegisterPlugin("plugin-a", ctx =>
        {
            ctx.RegisterDecoder(MakeDecoder("xml"));
        });

        var registry = builder.Build();

        registry.Decoders.Should().HaveCount(1);
        registry.Decoders.Should().ContainKey("json");

        registry.Diagnostics.Should().Contain(d =>
            d.Source == "plugin-a" &&
            d.Severity == DiagnosticSeverity.Error &&
            d.Message.ToLowerInvariant().Contains("duplicate"));
    }

    [Test]
    public void RegisterPlugin_CanRegisterMultipleCapabilities()
    {
        var builder = new PluginRegistryBuilder();

        builder.RegisterPlugin("my-plugin", ctx =>
        {
            ctx.RegisterDetector(MakeDetector("json", 100));
            ctx.RegisterDecoder(MakeDecoder("json"));
            ctx.RegisterEncoder(MakeEncoder("json"));
            ctx.RegisterTopologyExtractor(MakeTopologyExtractor("json"));
            ctx.RegisterTemplateProvider(MakeTemplateProvider("json"));
        });

        var registry = builder.Build();

        registry.Detectors.Should().HaveCount(1);
        registry.Decoders.Should().ContainKey("json");
        registry.Encoders.Should().ContainKey("json");
        registry.TopologyExtractors.Should().ContainKey("json");
        registry.TemplateProviders.Should().ContainKey("json");
    }

    // --- RegisterPlugin exception safety ---

    [Test]
    public void RegisterPlugin_ThrowingConfigure_RestoresPluginId()
    {
        var builder = new PluginRegistryBuilder();

        builder.RegisterPlugin("plugin-a", ctx =>
        {
            ctx.RegisterDecoder(MakeDecoder("json"));
        });

        Assert.Throws<InvalidOperationException>(() =>
            builder.RegisterPlugin("throwing-plugin", ctx =>
            {
                ctx.RegisterDecoder(MakeDecoder("xml"));
                throw new InvalidOperationException("registration failed");
            }));

        builder.RegisterPlugin("plugin-b", ctx =>
        {
            ctx.RegisterDecoder(MakeDecoder("xml"));
        });

        var registry = builder.Build();

        registry.Decoders.Should().HaveCount(2);
        registry.Decoders.Should().ContainKey("json");
        registry.Decoders.Should().ContainKey("xml");

        registry.Diagnostics.Should().NotContain(d =>
            d.Source == "plugin-b" &&
            d.Severity == DiagnosticSeverity.Error &&
            d.Message.ToLowerInvariant().Contains("duplicate"));
    }

    [Test]
    public void RegisterPlugin_ThrowingConfigure_DoesNotPoisonDuplicateState()
    {
        var builder = new PluginRegistryBuilder();

        Assert.Throws<InvalidOperationException>(() =>
            builder.RegisterPlugin("plugin-a", _ =>
                throw new InvalidOperationException("boom")));

        builder.RegisterPlugin("plugin-a", ctx =>
        {
            ctx.RegisterDecoder(MakeDecoder("json"));
        });

        var registry = builder.Build();

        registry.Decoders.Should().HaveCount(1);
        registry.Decoders.Should().ContainKey("json");

        registry.Diagnostics.Should().NotContain(d =>
            d.Source == "plugin-a" &&
            d.Severity == DiagnosticSeverity.Error);
    }

    [Test]
    public void RegisterPlugin_ThrowingConfigure_RollsBackPartialRegistrations()
    {
        var builder = new PluginRegistryBuilder();

        builder.RegisterPlugin("plugin-a", ctx =>
        {
            ctx.RegisterDecoder(MakeDecoder("json"));
        });

        Assert.Throws<InvalidOperationException>(() =>
            builder.RegisterPlugin("throwing-plugin", ctx =>
            {
                ctx.RegisterDecoder(MakeDecoder("xml"));
                ctx.RegisterEncoder(MakeEncoder("xml"));
                throw new InvalidOperationException("registration failed");
            }));

        var registry = builder.Build();

        registry.Decoders.Should().HaveCount(1);
        registry.Decoders.Should().ContainKey("json");
        registry.Decoders.Should().NotContainKey("xml");

        registry.Encoders.Should().BeEmpty();
    }

    [Test]
    public void RegisterPlugin_ThrowingConfigure_LaterValidPluginCanRegisterSameFormatId()
    {
        var builder = new PluginRegistryBuilder();

        Assert.Throws<InvalidOperationException>(() =>
            builder.RegisterPlugin("throwing-plugin", ctx =>
            {
                ctx.RegisterDecoder(MakeDecoder("xml"));
                throw new InvalidOperationException("registration failed");
            }));

        builder.RegisterPlugin("valid-plugin", ctx =>
        {
            ctx.RegisterDecoder(MakeDecoder("xml"));
        });

        var registry = builder.Build();

        registry.Decoders.Should().HaveCount(1);
        registry.Decoders.Should().ContainKey("xml");

        registry.Diagnostics.Should().NotContain(d =>
            d.Source == "valid-plugin" &&
            d.Severity == DiagnosticSeverity.Warning &&
            d.Message.ToLowerInvariant().Contains("disabled"));
    }

    [Test]
    public void RegisterPlugin_ThrowingConfigure_PreviousSuccessfulRegistrationsRemain()
    {
        var builder = new PluginRegistryBuilder();

        builder.RegisterPlugin("plugin-a", ctx =>
        {
            ctx.RegisterDecoder(MakeDecoder("json"));
            ctx.RegisterEncoder(MakeEncoder("json"));
            ctx.RegisterDetector(MakeDetector("json", 100));
        });

        var pluginADecoder = builder.Build().Decoders["json"];

        Assert.Throws<InvalidOperationException>(() =>
            builder.RegisterPlugin("throwing-plugin", ctx =>
            {
                ctx.RegisterDecoder(MakeDecoder("xml"));
                ctx.RegisterTopologyExtractor(MakeTopologyExtractor("xml"));
                ctx.RegisterTemplateProvider(MakeTemplateProvider("xml"));
                throw new InvalidOperationException("registration failed");
            }));

        var registry = builder.Build();

        registry.Decoders.Should().HaveCount(1);
        registry.Decoders["json"].Should().BeSameAs(pluginADecoder);

        registry.Encoders.Should().HaveCount(1);
        registry.Encoders.Should().ContainKey("json");

        registry.Detectors.Should().HaveCount(1);

        registry.TopologyExtractors.Should().BeEmpty();
        registry.TemplateProviders.Should().BeEmpty();
    }

    // --- Immutability: true snapshot ---

    [Test]
    public void Diagnostics_ImmutableSnapshot_NotAffectedBySubsequentBuilds()
    {
        var builder = new PluginRegistryBuilder();

        builder.SetCurrentPluginId("plugin-a");
        builder.RegisterDecoder(MakeDecoder("json"));

        var registry1 = builder.Build();

        builder.SetCurrentPluginId("plugin-b");
        builder.RegisterDecoder(MakeDecoder("xml"));

        var registry2 = builder.Build();

        registry1.Diagnostics.Should().BeEmpty();
        registry2.Diagnostics.Should().BeEmpty();
    }

    [Test]
    public void Diagnostics_SnapshotContainsConflictFromCurrentBuild()
    {
        var builder = new PluginRegistryBuilder();

        builder.SetCurrentPluginId("plugin-a");
        builder.RegisterDecoder(MakeDecoder("json"));

        builder.SetCurrentPluginId("plugin-b");
        builder.RegisterDecoder(MakeDecoder("json"));

        var registry = builder.Build();

        registry.Diagnostics.Should().Contain(d =>
            d.Source == "plugin-b" &&
            d.Severity == DiagnosticSeverity.Warning);
    }

    // --- Immutability ---

    [Test]
    public void Registry_CollectionsAreImmutable()
    {
        var builder = new PluginRegistryBuilder();
        builder.RegisterDetector(MakeDetector("a", 100));
        builder.RegisterDecoder(MakeDecoder("a"));
        builder.RegisterEncoder(MakeEncoder("a"));
        builder.RegisterTopologyExtractor(MakeTopologyExtractor("a"));
        builder.RegisterTemplateProvider(MakeTemplateProvider("a"));

        var registry = builder.Build();

        registry.Detectors.Should().BeAssignableTo<IReadOnlyList<IPayloadDetector>>();
        registry.Decoders.Should().BeAssignableTo<IReadOnlyDictionary<string, IPayloadDecoder>>();
        registry.Encoders.Should().BeAssignableTo<IReadOnlyDictionary<string, IPayloadEncoder>>();
        registry.TopologyExtractors.Should().BeAssignableTo<IReadOnlyDictionary<string, ITopologyExtractor>>();
        registry.TemplateProviders.Should().BeAssignableTo<IReadOnlyDictionary<string, IPayloadTemplateProvider>>();
        registry.Diagnostics.Should().BeAssignableTo<IReadOnlyList<PluginDiagnosticEntry>>();
    }

    // --- No registrations ---

    [Test]
    public void EmptyBuilder_ProducesEmptyRegistry()
    {
        var builder = new PluginRegistryBuilder();
        var registry = builder.Build();

        registry.Detectors.Should().BeEmpty();
        registry.Decoders.Should().BeEmpty();
        registry.Encoders.Should().BeEmpty();
        registry.TopologyExtractors.Should().BeEmpty();
        registry.TemplateProviders.Should().BeEmpty();
        registry.Diagnostics.Should().BeEmpty();
    }
}
