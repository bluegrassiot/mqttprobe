using Microsoft.Extensions.DependencyInjection;
using MqttProbe.UI.Components.Settings;
using MqttProbe.UI.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.UI.Tests.Components.Settings;

[TestFixture]
public class LicencesDialogTests : BunitTestContext
{
    private const string LicenseText =
        "                                 Apache License\n" +
        "                           Version 2.0, January 2004\n" +
        "                        https://www.apache.org/licenses/\n\n" +
        "   TERMS AND CONDITIONS FOR USE, REPRODUCTION, AND DISTRIBUTION\n";

    private const string NoticesText =
        "MQTT Probe\n" +
        "Copyright 2026 Bluegrass IoT\n\n" +
        "This product includes the following third-party components:\n\n" +
        "## Sparkplug B Protocol Buffer Generated Code\n" +
        "Licensed under the Eclipse Public License 2.0 (EPL-2.0).\n";

    private IRenderedComponent<MudDialogProvider> _provider = null!;

    [SetUp]
    public void Setup()
    {
        EnsureMudProviders();
        _provider = Render<MudDialogProvider>();
    }

    private async Task OpenAsync()
    {
        var parameters = new DialogParameters<LicencesDialog>
        {
            { x => x.LicenseText, LicenseText },
            { x => x.NoticesText, NoticesText }
        };
        var dialogService = Services.GetRequiredService<IDialogService>();

        await _provider.InvokeAsync(async () =>
            await dialogService.ShowAsync<LicencesDialog>(null, parameters));
    }

    [Test]
    public async Task Shows_The_App_Licence_And_Third_Party_Notices()
    {
        await OpenAsync();

        _provider.FindAll(".licences-dialog__heading")[0].TextContent.Trim().Should().Be("MQTTProbe licence");
        _provider.FindAll(".licences-dialog__heading")[1].TextContent.Trim().Should().Be("Third-party notices");
        _provider.Find("[data-testid=licence-text]").TextContent.Should().Contain("Apache License");
        _provider.Find("[data-testid=notices-text]").TextContent.Should().Contain("Eclipse Public License 2.0");
    }

    [Test]
    public async Task Keeps_Line_Breaks_And_Indentation_In_The_Legal_Text()
    {
        await OpenAsync();

        var license = _provider.Find("[data-testid=licence-text]").TextContent;
        license.Should().Contain("                                 Apache License");
        license.Should().Contain("Version 2.0, January 2004\n");
    }

    [Test]
    public async Task Body_Is_A_Focusable_Scroll_Region()
    {
        await OpenAsync();

        var body = _provider.Find(".licences-dialog__body");
        body.GetAttribute("tabindex").Should().Be("0");
        body.GetAttribute("role").Should().Be("region");
        body.GetAttribute("aria-label").Should().Be("Licence documents");
    }

    [Test]
    public async Task Dialog_Is_Named_By_Its_Visible_Title()
    {
        await OpenAsync();

        var titleId = _provider.Find(".licences-dialog__title").GetAttribute("id");
        titleId.Should().NotBeNullOrEmpty();
        _provider.Find(".mud-dialog").GetAttribute("aria-labelledby").Should().Be(titleId);
    }

    [Test]
    public async Task Close_Button_Closes_The_Viewer()
    {
        await OpenAsync();

        _provider.Find("[data-testid=licences-close]").Click();

        _provider.WaitForAssertion(() =>
            _provider.FindAll(".licences-dialog__title").Should().BeEmpty());
    }
}
