using MqttProbe.Core.Models.Configuration;
using MqttProbe.UI.Components.Settings;
using MqttProbe.UI.Tests.TestHelpers;
using MudBlazor;
using MudBlazor.Extensions;

namespace MqttProbe.UI.Tests.Components.Settings;

[TestFixture]
public class TimeSectionTests : BunitTestContext
{
    private RecordingUiSettings _settings = null!;

    [SetUp]
    public void Setup()
    {
        _settings = new RecordingUiSettings();
        Services.AddUiSettings(_settings);
        EnsureMudProviders();
    }

    private static MudSelect<string> Select(IRenderedComponent<TimeSection> cut, string label) =>
        cut.FindComponents<MudSelect<string>>().First(select => select.Instance.Label == label).Instance;

    private static string Text(AngleSharp.Dom.IElement element) =>
        string.Join(' ', element.TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    [TestCase(MessageTimePreferences.ZoneLocal, MessageTimePreferences.ClockFormat24Hour)]
    [TestCase(MessageTimePreferences.ZoneUtc, MessageTimePreferences.ClockFormat12Hour)]
    public void ZoneAndClockFormat_ReflectTheStoredPreferences(string zone, string clockFormat)
    {
        _settings.Ui.MessageTime = new MessageTimePreferences { Zone = zone, ClockFormat = clockFormat };

        var cut = Render<TimeSection>();

        Select(cut, "Time zone").GetState(select => select.Value).Should().Be(zone);
        Select(cut, "Clock format").GetState(select => select.Value).Should().Be(clockFormat);
    }

    [Test]
    public void FreshPreferences_UseTheCoreDefaultsWithoutSaving()
    {
        var defaults = new MessageTimePreferences();

        var cut = Render<TimeSection>();

        Select(cut, "Time zone").GetState(select => select.Value).Should().Be(defaults.Zone);
        Select(cut, "Clock format").GetState(select => select.Value).Should().Be(defaults.ClockFormat);
        _settings.SavedMessageTimes.Should().BeEmpty("opening Settings must not replace a stored choice");
    }

    [Test]
    public async Task ZoneChange_PersistsOnlyTheZoneAndKeepsTheBrowserScope()
    {
        _settings.Ui.MessageTime = new MessageTimePreferences
        {
            Mode = MessageTimePreferences.ModeDelta,
            Zone = MessageTimePreferences.ZoneLocal,
            ClockFormat = MessageTimePreferences.ClockFormat12Hour,
            DeltaScope = MessageTimePreferences.DeltaScopeTopic
        };
        var cut = Render<TimeSection>();

        var zone = Select(cut, "Time zone");

        await cut.InvokeAsync(() => zone.ValueChanged.InvokeAsync(MessageTimePreferences.ZoneUtc));

        var saved = _settings.SavedMessageTimes.Should().ContainSingle().Subject;
        saved.Zone.Should().Be(MessageTimePreferences.ZoneUtc);
        saved.Mode.Should().Be(MessageTimePreferences.ModeDelta);
        saved.ClockFormat.Should().Be(MessageTimePreferences.ClockFormat12Hour);
        saved.DeltaScope.Should().Be(MessageTimePreferences.DeltaScopeTopic);
        Select(cut, "Time zone").GetState(select => select.Value).Should().Be(MessageTimePreferences.ZoneUtc);
    }

    [Test]
    public async Task ClockFormatChange_PersistsOnlyTheFormat()
    {
        _settings.Ui.MessageTime = new MessageTimePreferences
        {
            Zone = MessageTimePreferences.ZoneUtc,
            ClockFormat = MessageTimePreferences.ClockFormat24Hour
        };
        var cut = Render<TimeSection>();

        var clockFormat = Select(cut, "Clock format");

        await cut.InvokeAsync(() =>
            clockFormat.ValueChanged.InvokeAsync(MessageTimePreferences.ClockFormat12Hour));

        var saved = _settings.SavedMessageTimes.Should().ContainSingle().Subject;
        saved.ClockFormat.Should().Be(MessageTimePreferences.ClockFormat12Hour);
        saved.Zone.Should().Be(MessageTimePreferences.ZoneUtc);
        Select(cut, "Clock format").GetState(select => select.Value)
            .Should().Be(MessageTimePreferences.ClockFormat12Hour);
    }

    [Test]
    public void Captions_ExplainTheDeviceZoneAndMilliseconds()
    {
        var cut = Render<TimeSection>();

        var captions = cut.FindAll(".settings-caption");
        captions.Should().HaveCount(2);
        Text(captions[0]).Should().Contain("this device's time zone");
        Text(captions[1]).Should().Contain("AM").And.Contain("Milliseconds");
    }

    [Test]
    public void Section_RendersNoPanelChrome()
    {
        var cut = Render<TimeSection>();

        cut.FindAll(".app-chrome-panel").Should().BeEmpty();
        cut.Markup.Should().NotContain("mud-typography-h6");
    }
}
