using AngleSharp.Dom;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Chart;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Metrics;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Core.Services.Security;
using MqttProbe.UI.Components.Browser.Connection;
using MqttProbe.UI.Tests.TestHelpers;
using MudBlazor;
using MudBlazor.Extensions;

namespace MqttProbe.UI.Tests.Components.Browser;

[TestFixture]
public class ConnectionDialogDirtyCloseTests : BunitTestContext
{
    private IConnectionSettings _connections = null!;
    private IRenderedComponent<MudDialogProvider> _dialogProvider = null!;
    private List<Connection> _savedConnections = null!;

    [SetUp]
    public void Setup()
    {
        var cfg = new AppConfiguration();
        _savedConnections = cfg.Connections.ToList();
        _connections = Substitute.For<IConnectionSettings>();
        _connections.Connections.Returns(_savedConnections);

        Services.AddSingleton(Substitute.For<IMqttManagedClient>());
        Services.AddConnectionSettings(_connections);
        var ui = Substitute.For<IUiSettings>();
        ui.Ui.Returns(cfg.Ui);
        Services.AddUiSettings(ui);
        Services.AddSingleton(Substitute.For<IMessageStoreManager>());
        Services.AddSingleton(Substitute.For<ISubscriptionManager>());
        var excludes = Substitute.For<ITopicExcludeService>();
        excludes.TopicExcludes.Returns(Array.Empty<string>());
        Services.AddSingleton(excludes);
        Services.AddSingleton(Substitute.For<ISessionState>());
        Services.AddSingleton(Substitute.For<IMqttOptionsBuilder>());
        Services.AddSingleton(Substitute.For<IBrokerStateResetCoordinator>());
        Services.AddSingleton(Substitute.For<ILogger<ConnectionDialog>>());
        var chart = Substitute.For<IChartDataService>();
        chart.StartAsync().Returns(Task.CompletedTask);
        Services.AddSingleton(chart);
        Services.AddSingleton(Substitute.For<IUxMetricsService>());
        var lifecycle = Substitute.For<IConnectionSessionLifecycle>();
        lifecycle.StopActiveConnectionAsync().Returns(Task.CompletedTask);
        Services.AddSingleton(lifecycle);
        Services.AddSingleton(Substitute.For<ICertificateAssetStore>());
        Services.AddSingleton(Substitute.For<ICertificateFilePicker>());
        var capability = Substitute.For<ICertificateInputCapability>();
        capability.UsesInputFileComponent.Returns(false);
        Services.AddSingleton(capability);

        EnsureMudProviders();
        _dialogProvider = Render<MudDialogProvider>();
    }

    [Test]
    public async Task Escape_OnDirtyForm_ShowsConfirmationInsideTheSameDialog()
    {
        await OpenDialogWithDirtyEdit();

        Escape();

        await _dialogProvider.WaitForAssertionAsync(() =>
            _dialogProvider.FindAll(".connection-dialog-confirm").Should().ContainSingle(
                "the guard must render inside the connection dialog's focus trap"));
        _dialogProvider.FindAll(".mud-dialog").Should().ContainSingle(
            "the guard must not open a second dialog");

        var confirm = _dialogProvider.Find(".connection-dialog-confirm");
        confirm.GetAttribute("role").Should().Be("alertdialog");
        confirm.GetAttribute("aria-modal").Should().Be("true");
        confirm.TextContent.Should().Contain("Unsaved changes");
        confirm.TextContent.Should().Contain("You have unsaved changes that will be discarded.");
        _dialogProvider.FindAll(".connection-dialog-content .connection-dialog-confirm").Should().ContainSingle(
            "the confirmation stays inside the dialog content the focus trap owns");

        var labels = _dialogProvider.FindAll(".connection-dialog-confirm button")
            .Select(button => button.TextContent.Trim());
        labels.Should().Equal("Cancel", "Discard");
        _dialogProvider.Find(".connection-dialog-body").HasAttribute("inert").Should().BeTrue(
            "the form behind the confirmation must leave the tab order");

        RenderedName().Should().Be("Modified",
            "the confirmation must not discard the edit until the user chooses");
        _dialogProvider.FindComponents<MudTextField<string>>()
            .First(field => field.Instance.Label == "Name")
            .Instance.GetState(field => field.Value).Should().Be("Modified");
    }

    [Test]
    public async Task Escape_WhileConfirmationShown_ReturnsToTheFormWithoutClosing()
    {
        await OpenDialogWithDirtyEdit();
        Escape();
        await _dialogProvider.WaitForAssertionAsync(() =>
            _dialogProvider.FindAll(".connection-dialog-confirm").Should().ContainSingle());

        Escape();
        await _dialogProvider.WaitForAssertionAsync(() =>
            _dialogProvider.FindAll(".connection-dialog-confirm").Should().BeEmpty(
                "a repeated Escape cancels the confirmation"));
        _dialogProvider.FindAll(".mud-dialog").Should().ContainSingle(
            "a repeated Escape must not discard and close");
        RenderedName().Should().Be("Modified");

        Escape();
        await _dialogProvider.WaitForAssertionAsync(() =>
            _dialogProvider.FindAll(".connection-dialog-confirm").Should().ContainSingle(
                "the guard still protects the dirty form after a cancelled confirmation"));
    }

    [Test]
    public async Task Cancel_KeepsTheSameFormInstanceAndEdits()
    {
        await OpenDialogWithDirtyEdit();
        var formBefore = _dialogProvider.FindComponent<MudForm>().Instance;
        Escape();
        await _dialogProvider.WaitForAssertionAsync(() =>
            _dialogProvider.FindAll(".connection-dialog-confirm").Should().ContainSingle());

        ConfirmButton("Cancel").Click();

        await _dialogProvider.WaitForAssertionAsync(() =>
            _dialogProvider.FindAll(".connection-dialog-confirm").Should().BeEmpty());
        _dialogProvider.FindAll(".mud-dialog").Should().ContainSingle();
        _dialogProvider.FindComponent<MudForm>().Instance.Should().BeSameAs(formBefore,
            "the form must stay mounted so focus and edits survive the confirmation");
        _dialogProvider.Find(".connection-dialog-body").HasAttribute("inert").Should().BeFalse(
            "the form is interactive again once the confirmation is cancelled");
        RenderedName().Should().Be("Modified");
    }

    [Test]
    public async Task Discard_ClosesTheDialog()
    {
        await OpenDialogWithDirtyEdit();
        Escape();
        await _dialogProvider.WaitForAssertionAsync(() =>
            _dialogProvider.FindAll(".connection-dialog-confirm").Should().ContainSingle());

        ConfirmButton("Discard").Click();

        await _dialogProvider.WaitForAssertionAsync(() =>
            _dialogProvider.FindAll(".mud-dialog").Should().BeEmpty("Discard closes the connection dialog"));
    }

    [Test]
    public async Task EscapeWhileFocusCaptureIsPending_CancelsThePendingCloseAttempt()
    {
        var module = SetUpDialogModule();
        var rememberFocus = module.SetupVoid("rememberFocus", _ => true);

        await OpenDialogWithDirtyEdit();
        var content = _dialogProvider.Find(".connection-dialog-content");

        var firstEscape = content.TriggerEventAsync("onkeydown", EscapeKey());
        rememberFocus.Invocations.Should().ContainSingle(
            "the first close attempt must still be waiting for focus capture");
        _dialogProvider.FindAll(".connection-dialog-confirm").Should().BeEmpty(
            "the prompt waits for the focus capture to finish");

        var secondEscape = content.TriggerEventAsync("onkeydown", EscapeKey());
        _dialogProvider.FindAll(".connection-dialog-confirm").Should().BeEmpty(
            "a second Escape cancels the attempt instead of installing a superseded prompt");

        rememberFocus.SetVoidResult();
        await Task.WhenAll(firstEscape, secondEscape).WaitAsync(TimeSpan.FromSeconds(2));

        _dialogProvider.FindAll(".connection-dialog-confirm").Should().BeEmpty(
            "the released capture must not show the cancelled prompt");
        _dialogProvider.FindAll(".mud-dialog").Should().ContainSingle();
        RenderedName().Should().Be("Modified");
    }

    [Test]
    public async Task DisposeWhileFocusCaptureIsPending_CompletesTheCloseAttempt()
    {
        var module = SetUpDialogModule();
        var rememberFocus = module.SetupVoid("rememberFocus", _ => true);

        var dialog = await OpenDialogWithDirtyEdit();
        var content = _dialogProvider.Find(".connection-dialog-content");

        var pendingEscape = content.TriggerEventAsync("onkeydown", EscapeKey());
        rememberFocus.Invocations.Should().ContainSingle("the close attempt must be waiting for focus capture");

        await _dialogProvider.InvokeAsync(async () => await dialog.DisposeAsync());

        rememberFocus.SetVoidResult();
        await pendingEscape.WaitAsync(TimeSpan.FromSeconds(2));

        _dialogProvider.FindAll(".connection-dialog-confirm").Should().BeEmpty(
            "a disposed dialog must not show a prompt from the released capture");
        RenderedName().Should().Be("Modified");
    }

    private static KeyboardEventArgs EscapeKey() => new() { Key = "Escape" };

    private string? RenderedName() =>
        _dialogProvider.FindComponents<MudTextField<string>>()
            .First(field => field.Instance.Label == "Name")
            .Instance.GetState(field => field.Value);

    private BunitJSModuleInterop SetUpDialogModule()
    {
        var module = JSInterop.SetupModule("./_content/MqttProbe.UI/js/connection-dialog-viewport.js");
        module.Mode = JSRuntimeMode.Strict;
        module.Setup<int?>("attach", _ => true).SetResult(1);
        module.SetupVoid("detach", _ => true).SetVoidResult();
        return module;
    }

    private async Task<ConnectionDialog> OpenDialogWithDirtyEdit()
    {
        var connection = new Connection { Name = "Test", Host = "localhost", Port = 1883 };
        _savedConnections.Add(connection);

        var dialogService = Services.GetRequiredService<IDialogService>();
        await _dialogProvider.InvokeAsync(async () =>
            await dialogService.ShowAsync<ConnectionDialog>("Connection Setup"));
        await _dialogProvider.InvokeAsync(async () =>
            await _dialogProvider.FindComponent<ConnectionDialog>().Instance.ConnectionChanged(connection));

        var nameField = _dialogProvider.FindComponents<MudTextField<string>>()
            .First(field => field.Instance.Label == "Name");
        nameField.Find("input").Input("Modified");
        return _dialogProvider.FindComponent<ConnectionDialog>().Instance;
    }

    private void Escape() => _dialogProvider.Find(".connection-dialog-content").KeyDown(key: "Escape");

    private IElement ConfirmButton(string text) =>
        _dialogProvider.FindAll(".connection-dialog-confirm button")
            .First(button => button.TextContent.Trim() == text);
}
