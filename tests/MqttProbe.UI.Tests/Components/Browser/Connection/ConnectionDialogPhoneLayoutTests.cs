using System.Text.RegularExpressions;
using AngleSharp.Dom;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.UI.Components.Browser.Connection;
using MqttProbe.UI.Tests.TestHelpers;

// Not "...Browser.Connection": a Connection namespace segment would shadow the Connection model for the existing tests.
namespace MqttProbe.UI.Tests.Components.Browser;

[TestFixture]
public class ConnectionDialogPhoneLayoutTests : BunitTestContext
{
    private const string ComponentsCss = "src/MqttProbe.UI/wwwroot/css/components.css";
    private const string DialogCss = "src/MqttProbe.UI/Components/Browser/Connection/ConnectionDialog.razor.css";
    private const string HeaderCss = "src/MqttProbe.UI/Components/Browser/Connection/ConnectionDialogHeader.razor.css";
    private const string DialogMarkup = "src/MqttProbe.UI/Components/Browser/Connection/ConnectionDialog.razor";
    private const string InteropClass = "src/MqttProbe.UI/Components/Browser/Connection/ConnectionDialogViewportInterop.cs";
    private const string ViewportScript = "src/MqttProbe.UI/wwwroot/js/connection-dialog-viewport.js";
    private const string KeyboardInsetVariable = "--connection-dialog-keyboard-inset";
    private const string TopInsetVariable = "--connection-dialog-viewport-top-inset";

    private const string PhoneMediaQuery = "@media (max-width: 599px)";
    private const string KeyboardEligibilityQuery = "@media (max-width: 599px), (pointer: coarse)";

    [SetUp]
    public void Setup() => EnsureMudProviders();

    [Test]
    public void PhoneSheet_FillsTheViewport_InsteadOfUsingTheDesktopCardBox()
    {
        var sheet = ReadPhoneMediaBlockContaining(ComponentsCss, ".mud-dialog.connection-dialog-paper {");

        sheet.Should().Contain("width: 100%");
        sheet.Should().Contain("height: 100%");
        sheet.Should().Contain("max-width: 100%");
        sheet.Should().Contain("max-height: none");
        sheet.Should().Contain("border-radius: 0");

        sheet.Should().NotContain("560px");
        sheet.Should().NotContain("100dvh");
    }

    [Test]
    public void PhoneSheet_KeepsItsWidthBasedPresentationOnly()
    {
        var sheet = ReadPhoneMediaBlockContaining(ComponentsCss, ".mud-dialog.connection-dialog-paper {");

        sheet.Should().Contain("width: 100%");
        sheet.Should().Contain("height: 100%");
        sheet.Should().Contain("border-radius: 0");
        sheet.Should().NotContain("safe-area-inset", "device insets belong to the capability block");
        sheet.Should().NotContain("connection-dialog-keyboard-inset");
    }

    [Test]
    public void Chain_BoundsEveryMudBlazorSlot_BetweenThePaperAndTheContent()
    {
        var css = ReadRepositoryFile(ComponentsCss);
        var rule = ExtractBracedBlockFromFirstBrace(css[css.IndexOf("> div:not(.mud-dialog-title)", StringComparison.Ordinal)..]);

        rule.Should().Contain("flex: 1 1 auto");
        rule.Should().Contain("min-height: 0");
        rule.Should().Contain("display: flex");
        css.Should().Contain(".mud-dialog.connection-dialog-paper .mud-focus-trap-child-container");
        css.Should().Contain(".mud-dialog.connection-dialog-paper .mud-dialog-content");
    }

    [Test]
    public void PaperHook_BoundsTheChainAtEveryHeight()
    {
        var css = ReadRepositoryFile(ComponentsCss);

        Regex.Matches(css, Regex.Escape(".mud-dialog.connection-dialog-paper {"))
            .Count.Should().Be(3);

        ReadCssRule(ComponentsCss, ".mud-dialog.connection-dialog-paper").Should().Contain("overflow: hidden");
        ReadMediaBlockContaining(ComponentsCss, KeyboardEligibilityQuery, ".mud-dialog.connection-dialog-paper {")
            .Should().Contain("safe-area-inset-left");
        ReadPhoneMediaBlockContaining(ComponentsCss, ".mud-dialog.connection-dialog-paper {")
            .Should().Contain("border-radius: 0");
    }

    [Test]
    public void PhoneContent_FillsTheSheet_InsteadOfHoldingTheDesktopHeight()
    {
        var phoneBlock = ReadPhoneMediaBlockContaining(DialogCss, ".connection-dialog-content {");

        phoneBlock.Should().Contain("flex: 1 1 auto");
        phoneBlock.Should().Contain("min-height: 0");
        phoneBlock.Should().Contain("height: auto");
        phoneBlock.Should().Contain("max-height: none");
    }

    [Test]
    public void PersistentHeader_NeverShrinksBelowThePanel_AndTightensOnPhones()
    {
        ReadCssRule(HeaderCss, ".connection-dialog-header").Should().Contain("flex-shrink: 0");
        ReadPhoneMediaBlockContaining(HeaderCss, ".connection-dialog-header {")
            .Should().Contain("padding: 8px 12px");
    }

    [Test]
    public void StepSelector_LabelDoesNotWrapOnTheNarrowestPhone()
    {
        ReadCssRule(DialogCss, ".step-btn").Should().Contain("white-space: nowrap");
    }

    [Test]
    public void HeaderSelector_SizesTheElementMudSelectRenders_NotTheClassItIsGiven()
    {
        // A bare ".header-select" rule can never match: MudSelect puts the class it is given on the input inside its own root.
        ReadRepositoryFile(HeaderCss).Should().NotContain(".header-select");

        ReadCssRule(HeaderCss, ".connection-dialog-header ::deep .header-label")
            .Should().Contain("flex-shrink: 0");
        ReadCssRule(HeaderCss, ".connection-dialog-header ::deep .mud-select")
            .Should().Contain("flex: 1");

        var phoneSelector = ReadPhoneMediaBlockContaining(
            HeaderCss, ".connection-dialog-header ::deep .mud-select {");
        phoneSelector.Should().Contain("flex: 1 1 8rem");
        phoneSelector.Should().Contain("min-width: 0");

        var cut = Render<ConnectionDialogHeader>(p => p
            .Add(x => x.Connections, new List<Connection> { new Connection { Name = "one" } }));

        var flexItem = cut.FindAll("div.connection-dialog-header__main > *")
            .Single(element => element.ClassList.Contains("mud-select"));
        flexItem.Children.Should().Contain(element => element.ClassList.Contains("mud-input-control"),
            "the class MudSelect is given lands one level into its root");
    }

    [Test]
    public void ActiveTabPanel_IsTheSingleScrollOwner_AndNeverScrollsSideways()
    {
        var panel = ReadCssRule(
            DialogCss, ".connection-dialog-content ::deep .connection-tabs .mud-tab-panel[role='tabpanel'].mud-tab-panel-active");

        panel.Should().Contain("flex: 1");
        panel.Should().Contain("min-height: 0");
        panel.Should().Contain("overflow-y: auto");
        panel.Should().Contain("overflow-x: hidden");
    }

    [Test]
    public void ViewportScript_ListensToVisualViewportResizeAndScroll_CoalescedByAnimationFrame()
    {
        var script = ReadRepositoryFile(ViewportScript);

        script.Should().Contain("window.visualViewport");
        script.Should().Contain("addEventListener('resize'", "iOS reports the keyboard through resize *and* scroll");
        script.Should().Contain("addEventListener('scroll'");
        script.Should().Contain("requestAnimationFrame");
    }

    [Test]
    public void ViewportScript_RevealsTheFocusedControl_InsideTheScrollOwner()
    {
        var script = ReadRepositoryFile(ViewportScript);
        var reveal = ExtractBracedBlockFromFirstBrace(
            script[script.IndexOf("const revealFocusedField = () => {", StringComparison.Ordinal)..]);

        script.Should().Contain("const REVEAL_MARGIN = 8");
        script.Should().Contain("const scrollOwner = () =>");
        reveal.Should().Contain("const bandBottom = scrollerBox.bottom");
        reveal.Should().NotContain("connection-actions-footer");
        reveal.Should().Contain("const tallerThanBand = fieldBox.height > visibleBottom - visibleTop");
        reveal.Should().Contain("scroller.scrollTop = Math.max(0, scroller.scrollTop + (delta / scale))");
        reveal.Should().Contain("paperBox.width / paper.offsetWidth");
    }

    [Test]
    public void ViewportScript_WatchesTheLayout_SoChromeThatGrowsWithoutAnEventIsStillMeasured()
    {
        var script = ReadRepositoryFile(ViewportScript);

        script.Should().Contain("new ResizeObserver(schedule)");
        script.Should().Contain("observer?.observe(paper)");
        script.Should().Contain("observer?.observe(contentElement)");
        script.Should().Contain("observer?.observe(notices)");
        script.Should().Contain("observer?.disconnect()");
        script.Should().NotContain("SETTLE_FRAMES");
    }

    [Test]
    public void ViewportScript_MeasuresTheFitInLayoutSizes_NotPaintedRects()
    {
        var script = ReadRepositoryFile(ViewportScript);
        var fit = ExtractBracedBlockFromFirstBrace(
            script[script.IndexOf("const panelHeight = () => {", StringComparison.Ordinal)..]);

        fit.Should().Contain("paper.clientHeight");
        fit.Should().Contain("element.offsetHeight");
        fit.Should().NotContain("getBoundingClientRect");
        fit.Should().Contain(".step-selector");
        fit.Should().Contain(".mud-dialog-title");
        fit.Should().Contain(".connection-dialog-titlebar");
        fit.Should().Contain(".connection-dialog-header");
        fit.Should().Contain(".mud-tabs-tabbar");
        fit.Should().NotContain("connection-actions-footer");
        script.Should().Contain("const compact = panelHeight() < MIN_USABLE_PANEL_HEIGHT");
    }

    [Test]
    public void ViewportScript_TreatsPinchZoomAsZoom_NotAsAKeyboard()
    {
        var script = ReadRepositoryFile(ViewportScript);

        script.Should().Contain("const ZOOM_SCALE_THRESHOLD = 1.01");
        script.Should().Contain("viewport.scale > ZOOM_SCALE_THRESHOLD");
    }

    [Test]
    public void ViewportScript_RemovesEveryListenerItAdded()
    {
        var script = ReadRepositoryFile(ViewportScript);

        script.Should().Contain("removeEventListener('resize'");
        script.Should().Contain("removeEventListener('scroll'");
        script.Should().Contain("removeEventListener('orientationchange'");
        script.Should().Contain("removeEventListener('change'");
        script.Should().Contain("removeEventListener('focusin'");
        script.Should().Contain("cancelAnimationFrame");
        script.Should().Contain("export function detach(attachment)");
    }

    [Test]
    public void ViewportScript_ReleasesOnlyTheAttachmentItIsGiven()
    {
        var script = ReadRepositoryFile(ViewportScript);

        script.Should().Contain("activeAttachments.set(attachment, teardown)");
        script.Should().Contain("activeAttachments.get(attachment)");
        script.Should().NotContain("activeAttachments.clear()",
            "detach must never sweep another dialog's attachment");
    }

    [Test]
    public void ViewportScript_MeasuresBothHiddenStripsOfTheVisualViewport()
    {
        var script = ReadRepositoryFile(ViewportScript);

        script.Should().Contain("top: Math.max(0, Math.round(viewport.offsetTop))");
        script.Should().Contain("window.innerHeight - (viewport.height + viewport.offsetTop)");
    }

    [Test]
    public void ViewportScript_GatesKeyboardWork_OnCapabilityNotWidth()
    {
        var script = ReadRepositoryFile(ViewportScript);

        script.Should().Contain("const NARROW_MEDIA_QUERY = '(max-width: 599px)'");
        script.Should().Contain("const COARSE_POINTER_MEDIA_QUERY = '(pointer: coarse)'");
        script.Should().Contain("narrow.matches || coarsePointer.matches");
        script.Should().Contain("addEventListener('change'", "capability changes must re-evaluate");
        script.Should().NotContain("navigator.userAgent");
        script.Should().NotContain("navigator.platform");
    }

    [Test]
    public void ViewportScript_CountsEveryPersistentHeaderRowInTheChromeThePanelNeverGets()
    {
        var script = ReadRepositoryFile(ViewportScript);
        var chrome = ExtractBracedBlockFromFirstBrace(
            script[script.IndexOf("const panelHeight = () => {", StringComparison.Ordinal)..]);

        chrome.Should().Contain(".step-selector");
        chrome.Should().Contain(".mud-dialog-title");
        chrome.Should().Contain(".connection-dialog-titlebar");
        chrome.Should().Contain(".connection-dialog-header");
        chrome.Should().Contain(".mud-tabs-tabbar");
        chrome.Should().NotContain("connection-actions-footer");
    }

    [Test]
    public void ViewportScript_PublishesTheVariables_TheStylesheetReads()
    {
        var script = ReadRepositoryFile(ViewportScript);
        var sheet = ReadRepositoryFile(ComponentsCss);

        foreach (var variable in new[] { KeyboardInsetVariable, TopInsetVariable })
        {
            script.Should().Contain(variable);
            sheet.Should().Contain(variable);
        }
    }

    [Test]
    public void KeyboardIsUp_CardIsPlacedInTheVisibleBand_NotPaddedInsideMudBlazorsReservation()
    {
        var css = ReadRepositoryFile(ComponentsCss);
        var container = ExtractBracedBlockFromFirstBrace(
            css[css.IndexOf(".mud-dialog-container.connection-dialog-container--keyboard {", StringComparison.Ordinal)..]);

        container.Should().Contain("align-items: stretch");
        container.Should().Contain($"var({TopInsetVariable}, 0px)");
        container.Should().Contain($"var({KeyboardInsetVariable}, 0px)");
        container.Should().Contain("safe-area-inset-top");
        container.Should().Contain("safe-area-inset-bottom");

        var card = ExtractBracedBlockFromFirstBrace(
            css[css.IndexOf(".connection-dialog-container--keyboard .connection-dialog-paper", StringComparison.Ordinal)..]);
        card.Should().Contain("max-height: none");
        card.Should().Contain("height: auto");
        card.Should().Contain("margin: 0");
        card.Should().Contain("padding: 0");

        var wrapperPadding = ExtractBracedBlockFromFirstBrace(
            css[css.IndexOf(".connection-dialog-container--keyboard .mud-dialog-content", StringComparison.Ordinal)..]);
        wrapperPadding.Should().Contain("padding-top: 0");
        wrapperPadding.Should().Contain("padding-bottom: 0");

        var script = ReadRepositoryFile(ViewportScript);
        script.Should().Contain("container.style.setProperty(TOP_INSET_VARIABLE");
        script.Should().Contain("container.style.setProperty(BOTTOM_INSET_VARIABLE");
        script.Should().Contain("container.classList.toggle(CONTAINER_KEYBOARD_CLASS, hidden)");
    }

    [Test]
    public void Dialog_HandsThePaperHookAndTheViewportScriptToTheBrowser()
    {
        var markup = ReadRepositoryFile(DialogMarkup);

        markup.Should().Contain("Class=\"connection-dialog-paper\"");
        markup.Should().Contain("@ref=\"_dialogContent\"");
        markup.Should().Contain("new ConnectionDialogViewportInterop(JS)");

        ReadRepositoryFile(InteropClass)
            .Should().Contain("./_content/MqttProbe.UI/js/connection-dialog-viewport.js");
    }

    [Test]
    public void ConnectionFields_CarryKeyboardAndCasingHints_ForPhoneEntry()
    {
        var cut = Render<ConnectionFields>(p => p.Add(x => x.Connection, new Connection()));

        var host = FindInputForLabel(cut, "Host");
        host.GetAttribute("inputmode").Should().Be("url");
        host.GetAttribute("autocapitalize").Should().Be("none");
        host.GetAttribute("autocorrect").Should().Be("off");

        FindInputForLabel(cut, "Port").GetAttribute("inputmode").Should().Be("numeric");

        // MQTT usernames are case-sensitive, so autocapitalise would corrupt them.
        var user = FindInputForLabel(cut, "User");
        user.GetAttribute("autocapitalize").Should().Be("none");
        user.GetAttribute("autocorrect").Should().Be("off");

        // A stored site login must not be filled in, and the field must not read as a username to autofill.
        user.GetAttribute("autocomplete").Should().Be("off");
        user.GetAttribute("name").Should().Be("mqtt-user");

        // The broker password is a new secret, not the site's account password.
        FindInputForLabel(cut, "Password").GetAttribute("autocomplete").Should().Be("new-password");
    }

    [Test]
    public void ConnectionFields_WebSocketBasePath_UsesTheUrlKeyboardAndCasingHints()
    {
        var cut = Render<ConnectionFields>(p =>
            p.Add(x => x.Connection, new Connection { Protocol = Protocol.WebSocket }));

        // MQTT paths are case-sensitive, so the base path must not be auto-capitalised or corrected.
        var basePath = FindInputForLabel(cut, "WebSocket Base Path");
        basePath.GetAttribute("inputmode").Should().Be("url");
        basePath.GetAttribute("autocapitalize").Should().Be("none");
        basePath.GetAttribute("autocorrect").Should().Be("off");
    }

    [Test]
    public void KeyboardStrips_FollowCapability_NotWidthOrDevice()
    {
        var css = ReadRepositoryFile(ComponentsCss);
        css.Should().Contain($"{KeyboardEligibilityQuery} {{");
        css.Should().NotContain("max-height: 520px");

        var block = ReadMediaBlockContaining(ComponentsCss, KeyboardEligibilityQuery, ".mud-dialog.connection-dialog-paper {");
        var normalized = Regex.Replace(block, @"\s+", " ");
        normalized.Should().Contain("padding-top: env(safe-area-inset-top, 0px);");
        normalized.Should().Contain("padding-bottom: env(safe-area-inset-bottom, 0px);");
        normalized.Should().Contain("padding-left: env(safe-area-inset-left, 0px);");
        normalized.Should().Contain("padding-right: env(safe-area-inset-right, 0px);");
        normalized.Should().Contain(
            $"padding-top: calc( env(safe-area-inset-top, 0px) + var({TopInsetVariable}, 0px) );");
        normalized.Should().Contain(
            $"padding-bottom: calc( env(safe-area-inset-bottom, 0px) + var({KeyboardInsetVariable}, 0px) );");
    }

    [Test]
    public void Card_ShrinksToTheAvailableSpace_InsteadOfTheViewport()
    {
        var content = ReadCssRule(DialogCss, ".connection-dialog-content");

        content.Should().Contain("height: 560px", "the preferred desktop size is unchanged");
        content.Should().Contain("max-height: 100%");
        content.Should().Contain("flex: 0 1 auto");
        content.Should().NotContain("dvh", "the card is bounded by its paper, not the viewport");

        ReadCssRule(ComponentsCss, ".mud-dialog.connection-dialog-paper")
            .Should().Contain("overflow: hidden");
        ReadRepositoryFile(ComponentsCss)
            .Should().Contain(".mud-dialog.connection-dialog-paper > div:not(.mud-dialog-title)");
    }

    [Test]
    public void CompactFallback_TheFormScrollsTheFields_WhileTheHeaderStaysPut()
    {
        var css = ReadRepositoryFile(DialogCss);

        var compactContent = ExtractBracedBlockFromFirstBrace(
            css[css.IndexOf(".connection-dialog-content--compact {", StringComparison.Ordinal)..]);
        compactContent.Should().Contain("overflow: hidden",
            "the card must not scroll, so the selector and action cannot scroll away");
        compactContent.Should().NotContain("overflow-y: auto");
        css.Should().NotContain("position: sticky");
        css.Should().NotContain("connection-dialog-footer-height");

        var compactForm = ExtractBracedBlockFromFirstBrace(
            css[css.IndexOf(".connection-dialog-content--compact ::deep .mud-form {", StringComparison.Ordinal)..]);
        compactForm.Should().Contain("flex: 1 1 0");
        compactForm.Should().Contain("overflow-y: auto");
        compactForm.Should().Contain("overflow-x: hidden");

        var compactPanel = ExtractBracedBlockFromFirstBrace(
            css[css.IndexOf(
                ".connection-dialog-content--compact ::deep .connection-tabs .mud-tab-panel[role='tabpanel'].mud-tab-panel-active",
                StringComparison.Ordinal)..]);
        compactPanel.Should().Contain("overflow: hidden");
        css.Should().Contain("::deep .connection-tabs .mud-tab-panel[role='tabpanel'].mud-tab-panel-active",
            "the panel stops being a second scroll owner in the fallback");

        var script = ReadRepositoryFile(ViewportScript);
        script.Should().Contain("MIN_USABLE_PANEL_HEIGHT");
        script.Should().Contain("container.classList.toggle(CONTAINER_KEYBOARD_CLASS, hidden)");
        script.Should().NotContain("FOOTER_HEIGHT_VARIABLE");
    }

    [Test]
    public void CompactFallback_PanelClipsBothAxes_SoTheFormIsTheOnlyScrollOwner()
    {
        var css = ReadRepositoryFile(DialogCss);
        var compactPanel = ExtractBracedBlockFromFirstBrace(
            css[css.IndexOf(
                ".connection-dialog-content--compact ::deep .connection-tabs .mud-tab-panel[role='tabpanel'].mud-tab-panel-active",
                StringComparison.Ordinal)..]);

        compactPanel.Should().Contain("overflow: hidden");
        compactPanel.Should().NotContain("overflow-y: visible");
        compactPanel.Should().Contain("flex: 0 0 auto");

        var compactStepSelector = ExtractBracedBlockFromFirstBrace(
            css[css.IndexOf(".connection-dialog-content--compact .step-selector", StringComparison.Ordinal)..]);
        compactStepSelector.Should().Contain("flex: 0 0 auto",
            "a direct child of the scrolling form must not shrink away in the fallback");
    }

    [Test]
    public void Notices_HostIsAlwaysInTheMarkup_WithTheAlertsInsideIt()
    {
        var markup = ReadRepositoryFile(DialogMarkup);

        var formClose = markup.IndexOf("</MudForm>", StringComparison.Ordinal);
        var host = markup.IndexOf("<div class=\"connection-dialog-notices\">", StringComparison.Ordinal);
        var collision = markup.IndexOf("@if (_nameCollisionError is not null)", StringComparison.Ordinal);
        var failure = markup.IndexOf("@if (_connectionState == ConnectionState.Failed)", StringComparison.Ordinal);

        host.Should().BeGreaterThan(formClose, "the alerts follow the scrolling form");
        collision.Should().BeGreaterThan(host, "the alerts belong inside the host");
        failure.Should().BeGreaterThan(host, "the alerts belong inside the host");
        markup[Math.Max(0, host - 120)..host].Should().NotContain("@if");
        markup[Math.Max(0, host - 120)..host].Should().NotContain("{");
    }

    [Test]
    public void PersistentHeader_SitsAboveTheScrollingForm()
    {
        var markup = ReadRepositoryFile(DialogMarkup);

        var contentOpen = markup.IndexOf("class=\"connection-dialog-content\"", StringComparison.Ordinal);
        var titlebar = markup.IndexOf("class=\"connection-dialog-titlebar\"", StringComparison.Ordinal);
        var header = markup.IndexOf("<ConnectionDialogHeader", StringComparison.Ordinal);
        var formOpen = markup.IndexOf("<MudForm", StringComparison.Ordinal);
        var formClose = markup.IndexOf("</MudForm>", StringComparison.Ordinal);
        var notices = markup.IndexOf("class=\"connection-dialog-notices\"", StringComparison.Ordinal);

        contentOpen.Should().BeGreaterThanOrEqualTo(0);
        titlebar.Should().BeGreaterThan(contentOpen);
        header.Should().BeGreaterThan(titlebar);
        formOpen.Should().BeGreaterThan(header, "the selector and its action stay above the scrolling fields");
        notices.Should().BeGreaterThan(formClose);

        var betweenHeaderAndForm = markup[header..formOpen];
        betweenHeaderAndForm.Should().NotContain("data-testid=\"connection-connect\"",
            "the primary action lives in the header component, not in a competing copy");
        markup.Should().NotContain("connection-actions-footer");
    }

    private static IElement FindInputForLabel(IRenderedComponent<ConnectionFields> cut, string label)
    {
        var labelElement = cut.FindAll("label").Single(element => element.TextContent.Trim() == label);
        var inputId = labelElement.GetAttribute("for");
        inputId.Should().NotBeNullOrWhiteSpace($"the '{label}' field must label its input");

        return cut.Find($"input[id='{inputId}']");
    }

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

        var end = css.IndexOf('}', start);
        return css[start..(end + 1)];
    }

    private static string ReadPhoneMediaBlockContaining(string cssFilePath, string anchor) =>
        ReadMediaBlockContaining(cssFilePath, PhoneMediaQuery, anchor);

    private static string ReadMediaBlockContaining(string cssFilePath, string mediaQuery, string anchor)
    {
        var css = ReadRepositoryFile(cssFilePath);
        // Media queries can share a prefix, so the header has to be matched whole.
        var header = mediaQuery + " {";

        var start = 0;
        while ((start = css.IndexOf(header, start, StringComparison.Ordinal)) >= 0)
        {
            var block = ExtractBracedBlockFromFirstBrace(css[start..]);
            if (block.Contains(anchor, StringComparison.Ordinal))
                return block;

            start += block.Length;
        }

        throw new InvalidOperationException(
            $"expected a '{header}' block in {cssFilePath} containing '{anchor}'");
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

        throw new InvalidOperationException("unbalanced braces in the extracted CSS block");
    }
}
