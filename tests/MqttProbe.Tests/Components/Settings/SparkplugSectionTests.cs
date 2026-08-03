using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Components.Settings;
using MqttProbe.Models.Configuration;
using MqttProbe.Services.Configuration;
using MqttProbe.Shared.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.Shared.Tests.Components.Settings;

[TestFixture]
public class SparkplugSectionTests : BunitTestContext
{
    private ISettingsStore _mockStore = null!;

    [SetUp]
    public void Setup()
    {
        _mockStore = Substitute.For<ISettingsStore>();
        var cfg = new AppConfiguration
        {
            Ui = new UiPreferences { EnrichSparkplugAliasNames = true }
        };
        _mockStore.Config.Returns(cfg);
        _mockStore.Ui.Returns(cfg.Ui);
        Services.AddSettingsSubstitute(_mockStore);
        EnsureMudProviders();
    }

    [Test]
    public async Task EnrichAliasNames_Toggle_CallsSetter()
    {
        _mockStore.SetEnrichSparkplugAliasNamesAsync(Arg.Any<bool>()).Returns(Task.CompletedTask);
        var cut = Render<SparkplugSection>();

        var toggle = cut.FindComponents<MudSwitch<bool>>()
            .First(s => s.Instance.Label == "Enrich Sparkplug alias names");
        await cut.InvokeAsync(() => toggle.Instance.ValueChanged.InvokeAsync(false));

        await _mockStore.Received(1).SetEnrichSparkplugAliasNamesAsync(false);
    }

    [Test]
    public async Task AutoRequestRebirth_Toggle_CallsSetter()
    {
        _mockStore.SetAutoRequestSparkplugRebirthAsync(Arg.Any<bool>()).Returns(Task.CompletedTask);
        var cut = Render<SparkplugSection>();

        var toggle = cut.FindComponents<MudSwitch<bool>>()
            .First(s => s.Instance.Label == "Automatically request rebirth");
        await cut.InvokeAsync(() => toggle.Instance.ValueChanged.InvokeAsync(false));

        await _mockStore.Received(1).SetAutoRequestSparkplugRebirthAsync(false);
    }

    [Test]
    public void Section_RendersNoPanelChrome()
    {
        var cut = Render<SparkplugSection>();

        cut.FindAll(".app-chrome-panel").Should().BeEmpty();
        cut.Markup.Should().NotContain("mud-typography-h6");
    }
}
