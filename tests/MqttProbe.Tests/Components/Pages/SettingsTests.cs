using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MqttProbe.Components.Layout;
using MqttProbe.Models.Configuration;
using MqttProbe.Models.Plugins;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Platform;
using MqttProbe.Services.Plugins.Loading;
using MqttProbe.Services.Plugins.Packaging;
using MqttProbe.Services.Plugins.Pipeline;
using MqttProbe.Services.Plugins.Registry;
using MqttProbe.Services.Security;
using MqttProbe.Shared.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.Shared.Tests.Components.Pages;

[TestFixture]
public class SettingsTests : BunitTestContext
{
    private IUiSettings _mockUi = null!;
    private IPerformanceSettings _mockPerformance = null!;
    private Themes _themes = null!;
    private IAppInfoService _mockAppInfo = null!;
    private IUpdateService _mockUpdateService = null!;

    [SetUp]
    public void Setup()
    {
        var cfg = new AppConfiguration
        {
            Ui = new UiPreferences { Theme = "dark", FontAccessible = false, AutoResubscribe = true },
            Performance = new PerformanceSettings { MaxStoredMessages = 10_000, MaxMessagesPerSecond = 50_000 }
        };
        _mockUi = Substitute.For<IUiSettings>();
        _mockUi.Ui.Returns(cfg.Ui);
        _mockPerformance = Substitute.For<IPerformanceSettings>();
        _mockPerformance.Performance.Returns(cfg.Performance);
        _themes = new Themes();
        _mockAppInfo = Substitute.For<IAppInfoService>();
        _mockAppInfo.RequiresAuthentication.Returns(true);
        _mockUpdateService = Substitute.For<IUpdateService>();
        Services.AddUiSettings(_mockUi);
        Services.AddPerformanceSettings(_mockPerformance);
        Services.AddSingleton<IThemes>(_themes);
        Services.AddSingleton(_mockAppInfo);
        Services.AddSingleton(_mockUpdateService);
        RegisterPluginSettingsDependencies(Services);
        AuthorizationContext.SetAuthorized("admin").SetRoles(AppRoles.Admin);
        EnsureMudProviders();
    }

    internal static void RegisterPluginSettingsDependencies(IServiceCollection services)
    {
        var pluginConfig = new PluginConfig();
        var registry = new PluginRegistryBuilder().Build([], []);
        var pipeline = new PayloadPipeline(registry, NullLogger<PayloadPipeline>.Instance);
        var session = new PluginInstallSession();

        services.AddSingleton(session);
        services.AddSingleton(new PluginInventoryService(pluginConfig, pipeline, session));
        services.AddSingleton(new PluginPackageInstaller(
            pluginConfig, Substitute.For<IAppInfoService>(), session, new PluginArchiveLimits(), NullLoggerFactory.Instance));
        services.AddSingleton(new PluginReloadService(
            pluginConfig, pipeline, session, new PluginAssemblyCache(), NullLoggerFactory.Instance));
        services.AddSingleton(Substitute.For<IPluginPackagePicker>());
        services.AddSingleton(Substitute.For<IPluginInputCapability>());
    }

    [Test]
    public void Nav_RendersEverySection()
    {
        var cut = Render<MqttProbe.Components.Pages.Settings>();

        var items = cut.FindAll("nav[aria-label='Settings sections'] button");
        items.Should().HaveCount(7);
        items.Select(i => i.TextContent.Trim()).Should().Contain("Plugins");
    }

    [Test]
    public void Nav_OmitsAccount_WhenRequiresAuthenticationIsFalse()
    {
        _mockAppInfo.RequiresAuthentication.Returns(false);

        var cut = Render<MqttProbe.Components.Pages.Settings>();

        var items = cut.FindAll("nav[aria-label='Settings sections'] button");
        items.Should().HaveCount(6);
        items.Should().NotContain(i => i.TextContent.Contains("Account"));
    }

    [Test]
    public void DefaultSection_IsAppearance()
    {
        var cut = Render<MqttProbe.Components.Pages.Settings>();

        cut.FindComponents<MudSelect<string>>().Should().Contain(s => s.Instance.Label == "Theme");
        cut.FindComponents<MudNumericField<int>>().Should().NotContain(f => f.Instance.Label == "Max stored messages");
    }

    [Test]
    public void SelectingSection_SwapsRenderedContent()
    {
        var cut = Render<MqttProbe.Components.Pages.Settings>();

        var performance = cut.FindAll("nav[aria-label='Settings sections'] button")
            .First(b => b.TextContent.Contains("Performance"));
        performance.Click();

        cut.FindComponents<MudNumericField<int>>()
            .Should().Contain(f => f.Instance.Label == "Max stored messages");
        cut.FindComponents<MudSelect<string>>().Should().NotContain(s => s.Instance.Label == "Theme");
    }

    [Test]
    public void ActiveSection_MarkedWithAriaCurrent()
    {
        var cut = Render<MqttProbe.Components.Pages.Settings>();

        var current = cut.FindAll("nav[aria-label='Settings sections'] button[aria-current='page']");
        current.Should().ContainSingle()
            .Which.TextContent.Should().Contain("Appearance");
    }

    [Test]
    public void MobileSelect_SwapsRenderedContent()
    {
        var cut = Render<MqttProbe.Components.Pages.Settings>();

        var sectionSelect = cut.FindComponents<MudSelect<string>>()
            .First(s => s.Instance.Label == "Settings section");
        cut.InvokeAsync(() => sectionSelect.Instance.ValueChanged.InvokeAsync("performance"));

        cut.FindComponents<MudNumericField<int>>()
            .Should().Contain(f => f.Instance.Label == "Max stored messages");
    }

    [Test]
    public void MobileSelect_ContainsSameSectionsAsRail()
    {
        var cut = Render<MqttProbe.Components.Pages.Settings>();

        var sectionSelect = cut.FindComponents<MudSelect<string>>()
            .First(s => s.Instance.Label == "Settings section");
        var values = sectionSelect.FindComponents<MudSelectItem<string>>()
            .Select(i => i.Instance.Value);

        values.Should().BeEquivalentTo(
            "appearance", "subscriptions", "sparkplug", "performance", "plugins", "account", "about");
    }

    [Test]
    public void MobileSelect_OmitsAccount_WhenRequiresAuthenticationIsFalse()
    {
        _mockAppInfo.RequiresAuthentication.Returns(false);

        var cut = Render<MqttProbe.Components.Pages.Settings>();

        var sectionSelect = cut.FindComponents<MudSelect<string>>()
            .First(s => s.Instance.Label == "Settings section");
        var values = sectionSelect.FindComponents<MudSelectItem<string>>()
            .Select(i => i.Instance.Value);

        values.Should().NotContain("account");
    }

    [Test]
    public void SectionRouting_QueryParam_SelectsSection()
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/?tab=settings&section=performance");

        var cut = Render<MqttProbe.Components.Pages.Settings>();

        cut.FindComponents<MudNumericField<int>>()
            .Should().Contain(f => f.Instance.Label == "Max stored messages");
    }

    [Test]
    public void SectionRouting_UnknownSlug_FallsBackToFirstSection()
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/?tab=settings&section=performance");

        var cut = Render<MqttProbe.Components.Pages.Settings>();
        cut.FindComponents<MudNumericField<int>>()
            .Should().Contain(f => f.Instance.Label == "Max stored messages");

        cut.InvokeAsync(() => nav.NavigateTo("/?tab=settings&section=garbage"));

        cut.FindComponents<MudSelect<string>>().Should().Contain(s => s.Instance.Label == "Theme");
    }

    [Test]
    public void SectionRouting_HiddenSection_FallsBackToFirstSection()
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/?tab=settings&section=performance");

        var cut = Render<MqttProbe.Components.Pages.Settings>();
        cut.FindComponents<MudNumericField<int>>()
            .Should().Contain(f => f.Instance.Label == "Max stored messages");

        _mockAppInfo.RequiresAuthentication.Returns(false);
        cut.InvokeAsync(() => nav.NavigateTo("/?tab=settings&section=account"));

        cut.FindComponents<MudSelect<string>>().Should().Contain(s => s.Instance.Label == "Theme");
    }

    [Test]
    public void LocationChanged_AfterRender_UpdatesActiveSection()
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/?tab=settings");

        var cut = Render<MqttProbe.Components.Pages.Settings>();
        cut.FindComponents<MudNumericField<int>>()
            .Should().NotContain(f => f.Instance.Label == "Max stored messages");

        cut.InvokeAsync(() => nav.NavigateTo("/?tab=settings&section=performance"));

        cut.FindComponents<MudNumericField<int>>()
            .Should().Contain(f => f.Instance.Label == "Max stored messages");
    }

    [Test]
    public void SelectingSection_WritesQueryParam()
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/?tab=settings");

        var cut = Render<MqttProbe.Components.Pages.Settings>();
        cut.FindAll("nav[aria-label='Settings sections'] button")
            .First(b => b.TextContent.Contains("Performance"))
            .Click();

        nav.Uri.Should().Contain("section=performance");
        nav.Uri.Should().Contain("tab=settings");
    }
}
