using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.UI.Components.Settings;
using MqttProbe.UI.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.UI.Tests.Components.Settings;

[TestFixture]
public class SparkplugSectionTests : BunitTestContext
{
    private ISparkplugSettings _mockStore = null!;

    [SetUp]
    public void Setup()
    {
        _mockStore = Substitute.For<ISparkplugSettings>();
        var sparkplug = new SparkplugSettings { EnrichAliasNames = true };
        _mockStore.Sparkplug.Returns(sparkplug);
        Services.AddSparkplugSettings(_mockStore);
        EnsureMudProviders();
    }

    [Test]
    public async Task EnrichAliasNames_Toggle_CallsSetter()
    {
        _mockStore.SetEnrichAliasNamesAsync(Arg.Any<bool>()).Returns(Task.CompletedTask);
        var cut = Render<SparkplugSection>();

        var toggle = cut.FindComponents<MudSwitch<bool>>()
            .First(s => s.Instance.Label == "Enrich Sparkplug alias names");
        await cut.InvokeAsync(() => toggle.Instance.ValueChanged.InvokeAsync(false));

        await _mockStore.Received(1).SetEnrichAliasNamesAsync(false);
    }

    [Test]
    public async Task AutoRequestRebirth_Toggle_CallsSetter()
    {
        _mockStore.SetAutoRequestRebirthAsync(Arg.Any<bool>()).Returns(Task.CompletedTask);
        var cut = Render<SparkplugSection>();

        var toggle = cut.FindComponents<MudSwitch<bool>>()
            .First(s => s.Instance.Label == "Automatically request rebirth");
        await cut.InvokeAsync(() => toggle.Instance.ValueChanged.InvokeAsync(false));

        await _mockStore.Received(1).SetAutoRequestRebirthAsync(false);
    }

    [Test]
    public async Task AllowNodeReboot_Toggle_CallsSetter()
    {
        _mockStore.SetAllowNodeRebootAsync(Arg.Any<bool>()).Returns(Task.CompletedTask);
        var cut = Render<SparkplugSection>();

        var toggle = cut.FindComponents<MudSwitch<bool>>()
            .First(s => s.Instance.Label == "Allow node reboot");
        await cut.InvokeAsync(() => toggle.Instance.ValueChanged.InvokeAsync(true));

        await _mockStore.Received(1).SetAllowNodeRebootAsync(true);
    }

    [Test]
    public async Task RebirthCooldown_NumericField_CallsSetter()
    {
        _mockStore.SetRebirthCooldownSecondsAsync(Arg.Any<int>()).Returns(Task.CompletedTask);
        var cut = Render<SparkplugSection>();

        var field = cut.FindComponents<MudNumericField<int>>()
            .First(f => f.Instance.Label == "Rebirth cooldown (seconds)");
        await cut.InvokeAsync(() => field.Instance.ValueChanged.InvokeAsync(45));

        await _mockStore.Received(1).SetRebirthCooldownSecondsAsync(45);
    }

    [Test]
    public void RebirthCooldown_NumericField_HasCorrectConstraints()
    {
        var cut = Render<SparkplugSection>();

        var field = cut.FindComponents<MudNumericField<int>>()
            .First(f => f.Instance.Label == "Rebirth cooldown (seconds)");
        field.Instance.Min.Should().Be(5);
        field.Instance.Max.Should().Be(600);
        field.Instance.Step.Should().Be(5);
    }

    [Test]
    public void Section_RendersNoPanelChrome()
    {
        var cut = Render<SparkplugSection>();

        cut.FindAll(".app-chrome-panel").Should().BeEmpty();
        cut.Markup.Should().NotContain("mud-typography-h6");
    }
}
