using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Components.Settings;
using MqttProbe.Models.Configuration;
using MqttProbe.Services.Configuration;
using MqttProbe.Shared.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.Shared.Tests.Components.Settings;

[TestFixture]
public class SubscriptionsSectionTests : BunitTestContext
{
    private IUiSettings _mockStore = null!;

    [SetUp]
    public void Setup()
    {
        _mockStore = Substitute.For<IUiSettings>();
        var cfg = new AppConfiguration
        {
            Ui = new UiPreferences { AutoResubscribe = true }
        };
        _mockStore.Ui.Returns(cfg.Ui);
        Services.AddUiSettings(_mockStore);
        EnsureMudProviders();
    }

    [Test]
    public async Task AutoResubscribe_Toggle_CallsSetter()
    {
        _mockStore.SetAutoResubscribeAsync(Arg.Any<bool>()).Returns(Task.CompletedTask);
        var cut = Render<SubscriptionsSection>();

        var toggle = cut.FindComponents<MudSwitch<bool>>()
            .First(s => s.Instance.Label == "Auto-resubscribe on connect");
        await cut.InvokeAsync(() => toggle.Instance.ValueChanged.InvokeAsync(false));

        await _mockStore.Received(1).SetAutoResubscribeAsync(false);
    }

    [Test]
    public void Section_RendersNoPanelChrome()
    {
        var cut = Render<SubscriptionsSection>();

        cut.FindAll(".app-chrome-panel").Should().BeEmpty();
        cut.Markup.Should().NotContain("mud-typography-h6");
    }
}
