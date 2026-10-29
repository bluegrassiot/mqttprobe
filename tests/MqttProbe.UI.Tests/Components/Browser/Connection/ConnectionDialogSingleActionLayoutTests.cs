using AngleSharp.Dom;
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
public class ConnectionDialogSingleActionLayoutTests : BunitTestContext
{
    private const string DialogMarkup = "src/MqttProbe.UI/Components/Browser/Connection/ConnectionDialog.razor";
    private const string DialogCss = "src/MqttProbe.UI/Components/Browser/Connection/ConnectionDialog.razor.css";
    private const string HeaderMarkup = "src/MqttProbe.UI/Components/Browser/Connection/ConnectionDialogHeader.razor";
    private const string HeaderCss = "src/MqttProbe.UI/Components/Browser/Connection/ConnectionDialogHeader.razor.css";
    private const string ViewportScript = "src/MqttProbe.UI/wwwroot/js/connection-dialog-viewport.js";

    private IConnectionSettings _connections = null!;
    private IMqttManagedClient _client = null!;
    private IRenderedComponent<MudDialogProvider> _dialogProvider = null!;
    private List<Connection> _savedConnections = null!;

    [SetUp]
    public void Setup()
    {
        var cfg = new AppConfiguration();
        _savedConnections = cfg.Connections.ToList();
        _connections = Substitute.For<IConnectionSettings>();
        _connections.Connections.Returns(_savedConnections);

        _client = Substitute.For<IMqttManagedClient>();
        _client.StartAttempt(Arg.Any<MqttManagedClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => new MqttConnectAttempt(
                new MqttConnectAttemptId(1),
                new TaskCompletionSource<MqttConnectOutcome>().Task));
        _client.IsCurrentAttempt(Arg.Any<MqttConnectAttemptId>()).Returns(true);

        Services.AddSingleton(_client);
        Services.AddConnectionSettings(_connections);
        var ui = Substitute.For<IUiSettings>();
        ui.Ui.Returns(cfg.Ui);
        Services.AddUiSettings(ui);
        var messageStore = Substitute.For<IMessageStoreManager>();
        messageStore.Start().Returns(Task.CompletedTask);
        Services.AddSingleton(messageStore);
        Services.AddSingleton(Substitute.For<ISubscriptionManager>());
        var excludes = Substitute.For<ITopicExcludeService>();
        excludes.TopicExcludes.Returns(Array.Empty<string>());
        Services.AddSingleton(excludes);
        Services.AddSingleton(Substitute.For<ISessionState>());
        var options = Substitute.For<IMqttOptionsBuilder>();
        options.BuildAsync(Arg.Any<Connection>(), Arg.Any<CertificateSessionResource>())
            .Returns(Task.FromResult(new MqttManagedClientOptions
            {
                ClientOptions = new MQTTnet.MqttClientOptionsBuilder().WithTcpServer("localhost").Build()
            }));
        Services.AddSingleton(options);
        Services.AddSingleton(Substitute.For<IBrokerStateResetCoordinator>());
        Services.AddSingleton(Substitute.For<ILogger<ConnectionDialog>>());
        var chart = Substitute.For<IChartDataService>();
        chart.StartAsync().Returns(Task.CompletedTask);
        Services.AddSingleton(chart);
        Services.AddSingleton(Substitute.For<IUxMetricsService>());
        var lifecycle = Substitute.For<IConnectionSessionLifecycle>();
        lifecycle.StopActiveConnectionAsync().Returns(Task.CompletedTask);
        lifecycle.StopAttemptAsync(Arg.Any<MqttConnectAttemptId>(), Arg.Any<CertificateSessionResource?>())
            .Returns(Task.CompletedTask);
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
    public async Task ExactlyOneConnectAction_LivesInThePersistentHeader()
    {
        await OpenDialog(NewConnection("Primary"));

        _dialogProvider.FindAll("button[title='Connect']").Should().ContainSingle(
            "the dialog offers a single primary action, not one per layout");
        var header = _dialogProvider.Find(".connection-dialog-header");
        header.QuerySelector("button[title='Connect']").Should().NotBeNull(
            "the primary action sits in the header beside the saved selector");
        header.QuerySelector("button[title='Connect']")!.GetAttribute("data-testid")
            .Should().Be("connection-connect");
    }

    [Test]
    public async Task NoFooterActions_AreRendered()
    {
        await OpenDialog(NewConnection("Primary"));

        _dialogProvider.FindAll(".connection-actions-footer").Should().BeEmpty(
            "the footer no longer hosts an idle Connect or Cancel");
        _dialogProvider.FindAll("button").Should().NotContain(
            button => button.TextContent.Trim() == "Cancel",
            "Cancel is replaced by the guarded dialog close control");
    }

    [Test]
    public async Task SavedSelector_IsFollowedImmediatelyByConnect_InTheTabOrder()
    {
        await OpenDialogWithSelection(NewConnection("Primary"));

        var header = _dialogProvider.Find(".connection-dialog-header");
        var focusable = FocusableIn(header);
        var selectorIndex = focusable.FindIndex(element =>
            element.GetAttribute("aria-label") == "Saved connection");
        selectorIndex.Should().BeGreaterThanOrEqualTo(0,
            "the saved selector must expose a stable accessible name");
        selectorIndex.Should().BeLessThan(focusable.Count - 1,
            "Connect must follow the selector in the focusable order");
        focusable[selectorIndex + 1].GetAttribute("title").Should().Be("Connect",
            "Tab from the selector must reach Connect before any maintenance action");
    }

    [Test]
    public async Task SavedSelector_ExposesStableAccessibleNameAndClass()
    {
        await OpenDialog(NewConnection("Primary"));

        var selector = _dialogProvider.Find(".connection-dialog-selector");
        selector.Should().NotBeNull();
        selector.QuerySelector("input[aria-label='Saved connection']").Should().NotBeNull(
            "screen readers and browser acceptance both need the selector's name");
        selector.QuerySelector("[data-testid='saved-connection']").Should().NotBeNull();
    }

    [Test]
    public async Task Close_WhileDirty_ShowsTheInlineConfirmation_AndInertsHeaderControls()
    {
        var connection = NewConnection("Primary");
        await OpenDialogWithSelection(connection);
        DirtyNameField("Modified");

        _dialogProvider.Find("[data-testid=connection-close]").Click();

        await _dialogProvider.WaitForAssertionAsync(() =>
            _dialogProvider.FindAll(".connection-dialog-confirm").Should().ContainSingle(
                "the guarded close asks before discarding edits"));

        var body = _dialogProvider.Find(".connection-dialog-body");
        body.HasAttribute("inert").Should().BeTrue(
            "the persistent header and title bar must leave the tab order behind the confirmation");
        body.QuerySelector(".connection-dialog-header").Should().NotBeNull(
            "the header controls live inside the inert region");
        body.QuerySelector("[data-testid=connection-close]").Should().NotBeNull(
            "the close control is behind the confirmation too");
        body.QuerySelector("button[title='Connect']").Should().NotBeNull();

        _dialogProvider.FindAll(".connection-dialog-confirm button")
            .First(button => button.TextContent.Trim() == "Cancel").Click();

        await _dialogProvider.WaitForAssertionAsync(() =>
            _dialogProvider.FindAll(".connection-dialog-confirm").Should().BeEmpty(
                "Cancel returns to the form without discarding"));
        _dialogProvider.Find(".connection-dialog-body").HasAttribute("inert").Should().BeFalse();
        RenderedName().Should().Be("Modified");
    }

    [Test]
    public async Task Close_WhileDirty_ThenDiscard_ClosesTheDialog()
    {
        await OpenDialogWithSelection(NewConnection("Primary"));
        DirtyNameField("Modified");

        _dialogProvider.Find("[data-testid=connection-close]").Click();
        await _dialogProvider.WaitForAssertionAsync(() =>
            _dialogProvider.FindAll(".connection-dialog-confirm").Should().ContainSingle());

        _dialogProvider.FindAll(".connection-dialog-confirm button")
            .First(button => button.TextContent.Trim() == "Discard").Click();

        await _dialogProvider.WaitForAssertionAsync(() =>
            _dialogProvider.FindAll(".mud-dialog").Should().BeEmpty(
                "Discard closes the dialog through the guarded path"));
    }

    [Test]
    public async Task Close_WhileClean_ClosesTheDialog_FromItsOwnAccessibleControl()
    {
        await OpenDialogWithSelection(NewConnection("Primary"));

        var close = _dialogProvider.Find("[data-testid=connection-close]");
        close.GetAttribute("aria-label").Should().Be("Close");
        close.ClassList.Should().Contain("connection-dialog-close");
        close.GetAttribute("type").Should().Be("button");
        _dialogProvider.Find(".connection-dialog-titlebar [data-testid=connection-close]").Should().NotBeNull(
            "the close control belongs to the dialog title bar");
        var title = _dialogProvider.Find(".connection-dialog-title");
        title.TextContent.Trim().Should().Be("Setup Connection");
        var titleId = title.GetAttribute("id");
        titleId.Should().NotBeNullOrWhiteSpace("the title names this dialog instance");
        _dialogProvider.Find(".mud-dialog").GetAttribute("aria-labelledby").Should().Be(titleId);

        close.Click();

        await _dialogProvider.WaitForAssertionAsync(() =>
            _dialogProvider.FindAll(".mud-dialog").Should().BeEmpty("a clean close needs no confirmation"));
    }

    [Test]
    public async Task WhileConnecting_TheHeaderActionBecomesStop_AndKeepsTheConnectingStatus()
    {
        await OpenDialogWithSelection(NewConnection("Primary"));

        _dialogProvider.Find("button[title='Connect']").Click();
        var dialog = _dialogProvider.FindComponent<ConnectionDialog>().Instance;
        _dialogProvider.WaitForAssertion(() => dialog.IsConnectingForTests.Should().BeTrue(),
            TimeSpan.FromSeconds(2));

        var header = _dialogProvider.Find(".connection-dialog-header");
        var stop = header.QuerySelector("[data-testid=cancel-connect]");
        stop.Should().NotBeNull("the single action area becomes Stop while an attempt is pending");
        stop!.TextContent.Trim().Should().Be("Stop");
        header.QuerySelector("button[title='Connect']").Should().BeNull(
            "Connect must not compete with Stop");
        var status = header.QuerySelector("[data-testid=connection-connecting-status]");
        status.Should().NotBeNull();
        status!.GetAttribute("role").Should().Be("status");
        status.TextContent.Should().Contain("Connecting...");

        stop.Click();

        _dialogProvider.WaitForAssertion(() => dialog.IsConnectingForTests.Should().BeFalse(),
            TimeSpan.FromSeconds(2));
        header = _dialogProvider.Find(".connection-dialog-header");
        header.QuerySelector("button[title='Connect']").Should().NotBeNull(
            "Connect returns once the attempt is stopped");
        header.QuerySelector("[data-testid=cancel-connect]").Should().BeNull();
    }

    [Test]
    public async Task CompactFallback_KeepsTheHeaderOutsideTheFieldScroller()
    {
        await OpenDialogWithSelection(NewConnection("Primary"));

        var header = _dialogProvider.Find(".connection-dialog-header");
        header.Closest(".mud-form").Should().BeNull(
            "the selector and its action must not scroll away with the fields");
        _dialogProvider.Find(".mud-form .mud-tab-panel-active").Should().NotBeNull(
            "the compact field scroller still owns the form's tab content");

        var markup = ReadRepositoryFile(DialogMarkup);
        var titlebar = markup.IndexOf("class=\"connection-dialog-titlebar\"", StringComparison.Ordinal);
        var headerMarkup = markup.IndexOf("<ConnectionDialogHeader", StringComparison.Ordinal);
        var form = markup.IndexOf("<MudForm", StringComparison.Ordinal);
        var notices = markup.IndexOf("class=\"connection-dialog-notices\"", StringComparison.Ordinal);

        titlebar.Should().BeGreaterThanOrEqualTo(0);
        headerMarkup.Should().BeGreaterThan(titlebar);
        form.Should().BeGreaterThan(headerMarkup, "the form starts below the persistent chrome");
        notices.Should().BeGreaterThan(form);

        var css = ReadRepositoryFile(DialogCss);
        var compactContent = ExtractBracedBlockFromFirstBrace(
            css[css.IndexOf(".connection-dialog-content--compact {", StringComparison.Ordinal)..]);
        compactContent.Should().Contain("overflow: hidden",
            "the card must not scroll when the compact header is pinned");
        var compactForm = ExtractBracedBlockFromFirstBrace(
            css[css.IndexOf(".connection-dialog-content--compact ::deep .mud-form {", StringComparison.Ordinal)..]);
        compactForm.Should().Contain("flex: 1 1 0");
        compactForm.Should().Contain("overflow-y: auto");
        compactForm.Should().Contain("overflow-x: hidden",
            "long values must not introduce a horizontal scrollbar");

        var script = ReadRepositoryFile(ViewportScript);
        var scrollOwner = ExtractBracedBlockFromFirstBrace(
            script[script.IndexOf("const scrollOwner = () => {", StringComparison.Ordinal)..]);
        scrollOwner.Should().Contain("querySelector('.mud-form')");
    }

    [Test]
    public async Task TwoDialogs_GetDistinctTitleIds()
    {
        await OpenDialogWithSelection(NewConnection("Primary"));
        await OpenDialog(NewConnection("Second"));

        var dialogs = _dialogProvider.FindAll(".mud-dialog");
        dialogs.Should().HaveCount(2);

        var titleIds = new List<string?>();
        foreach (var dialog in dialogs)
        {
            var titleId = dialog.GetAttribute("aria-labelledby");
            titleId.Should().NotBeNullOrWhiteSpace();
            dialog.QuerySelector($".connection-dialog-title[id='{titleId}']").Should().NotBeNull(
                "each dialog must be named by its own title element");
            titleIds.Add(titleId);
        }

        titleIds.Should().OnlyHaveUniqueItems("duplicate ids would make the dialog names ambiguous");
    }

    [Test]
    public void RemovedFooter_HasNoMarkupOrStylesLeft()
    {
        ReadRepositoryFile(DialogMarkup).Should().NotContain("connection-actions-footer");
        ReadRepositoryFile(DialogCss).Should().NotContain("connection-actions-footer");
        ReadRepositoryFile(DialogCss).Should().NotContain("position: sticky");
    }

    [Test]
    public void Header_DomOrderIsSelectorThenConnectThenMaintenanceActions()
    {
        var header = ReadRepositoryFile(HeaderMarkup);
        var selector = header.IndexOf("connection-dialog-selector", StringComparison.Ordinal);
        var connect = header.IndexOf("connection-dialog-connect", StringComparison.Ordinal);
        var maintenance = header.IndexOf("title=\"New connection\"", StringComparison.Ordinal);

        selector.Should().BeGreaterThanOrEqualTo(0);
        connect.Should().BeGreaterThan(selector, "Connect follows the selector in the tab order");
        maintenance.Should().BeGreaterThan(connect,
            "saved-connection maintenance stays after the primary action");
        header.Should().Contain("data-testid=\"connection-connect\"");
    }

    [Test]
    public void PersistentHeader_DoesNotShrinkOrScrollWithTheFields()
    {
        ReadCssRule(HeaderCss, ".connection-dialog-header").Should().Contain("flex-shrink: 0");
        ReadCssRule(DialogCss, ".connection-dialog-titlebar").Should().Contain("flex-shrink: 0");

        var selector = ReadCssRule(HeaderCss, ".connection-dialog-header ::deep .mud-select");
        selector.Should().Contain("flex: 1 1 10rem");
        selector.Should().Contain("min-width: 0");
    }

    [Test]
    public void PhoneHeader_WrapsTheActionBelowTheSelector_KeepingOneAction()
    {
        var phone = ReadMediaBlockContaining(HeaderCss, "@media (max-width: 599px)");

        phone.Should().Contain(".connection-dialog-header__main {");
        phone.Should().Contain("flex-wrap: wrap");
        phone.Should().Contain(".connection-dialog-header__action {");
        phone.Should().Contain("flex: 1 1 100%");
        phone.Should().Contain("width: 100%");
    }

    [Test]
    public void ViewportScript_NoLongerReservesSpaceForARemovedFooter()
    {
        var script = ReadRepositoryFile(ViewportScript);

        script.Should().NotContain("connection-actions-footer");
        script.Should().NotContain("FOOTER_HEIGHT_VARIABLE");
        script.Should().NotContain("footer-height");

        var chrome = ExtractBracedBlockFromFirstBrace(
            script[script.IndexOf("const panelHeight = () => {", StringComparison.Ordinal)..]);
        chrome.Should().Contain(".connection-dialog-titlebar");
        chrome.Should().NotContain("connection-actions-footer");
    }

    [Test]
    public void CompactFallback_LeavesOneScrollOwner_WithoutAStickyActionBand()
    {
        var css = ReadRepositoryFile(DialogCss);
        var compactPanel = ExtractBracedBlockFromFirstBrace(
            css[css.IndexOf(
                ".connection-dialog-content--compact ::deep .connection-tabs .mud-tab-panel[role='tabpanel'].mud-tab-panel-active",
                StringComparison.Ordinal)..]);

        compactPanel.Should().Contain("overflow: hidden");
        compactPanel.Should().NotContain("footer-height");
        css.Should().NotContain(".connection-dialog-content--compact .connection-actions-footer");
    }

    private void DirtyNameField(string value) =>
        _dialogProvider.FindComponents<MudTextField<string>>()
            .First(field => field.Instance.Label == "Name")
            .Find("input").Input(value);

    private static string ReadRepositoryFile(string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath));
        File.Exists(path).Should().BeTrue($"expected the repository file at '{path}' to exist");
        return File.ReadAllText(path);
    }

    private static string ReadCssRule(string cssFilePath, string selector)
    {
        var css = ReadRepositoryFile(cssFilePath);
        var start = css.IndexOf(selector + " {", StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"'{selector}' must be declared in {cssFilePath}");
        return css[start..(css.IndexOf('}', start) + 1)];
    }

    private static string ReadMediaBlockContaining(string cssFilePath, string mediaQuery)
    {
        var css = ReadRepositoryFile(cssFilePath);
        var start = css.IndexOf(mediaQuery, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"'{mediaQuery}' must be declared in {cssFilePath}");
        return ExtractBracedBlockFromFirstBrace(css[start..]);
    }

    private static string ExtractBracedBlockFromFirstBrace(string text)
    {
        var open = text.IndexOf('{');
        open.Should().BeGreaterThanOrEqualTo(0, "expected a '{' to open the block");

        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '{')
                depth++;
            else if (text[i] == '}' && --depth == 0)
                return text[..(i + 1)];
        }

        throw new InvalidOperationException("unbalanced braces in the extracted block");
    }

    private string? RenderedName() =>
        _dialogProvider.FindComponents<MudTextField<string>>()
            .First(field => field.Instance.Label == "Name")
            .Instance.GetState(field => field.Value);

    private async Task OpenDialog(params Connection[] connections)
    {
        _savedConnections.AddRange(connections);
        var dialogService = Services.GetRequiredService<IDialogService>();
        await _dialogProvider.InvokeAsync(async () =>
            await dialogService.ShowAsync<ConnectionDialog>("Setup Connection", ConnectionDialogTestOptions.Production));
    }

    private async Task<ConnectionDialog> OpenDialogWithSelection(Connection connection)
    {
        await OpenDialog(connection);
        var dialog = _dialogProvider.FindComponent<ConnectionDialog>().Instance;
        await _dialogProvider.InvokeAsync(async () => await dialog.ConnectionChanged(connection));
        return dialog;
    }

    private static List<IElement> FocusableIn(IElement root) =>
        root.QuerySelectorAll("button, input, select, textarea, a[href], [tabindex]")
            .Where(element => !element.HasAttribute("disabled"))
            .Where(element => element.GetAttribute("tabindex") != "-1")
            .Where(element => element.GetAttribute("type") != "hidden")
            .ToList();

    private static Connection NewConnection(string name) =>
        new() { Name = name, Host = "localhost", Port = 1883 };
}
