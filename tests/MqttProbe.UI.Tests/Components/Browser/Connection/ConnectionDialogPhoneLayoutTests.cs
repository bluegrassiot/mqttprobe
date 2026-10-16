using System.Text.RegularExpressions;
using AngleSharp.Dom;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.UI.Components.Browser.Connection;
using MqttProbe.UI.Tests.TestHelpers;

// Deliberately not "...Browser.Connection": a namespace segment named Connection
// would shadow the Connection model for the tests already in ...Components.Browser.
namespace MqttProbe.UI.Tests.Components.Browser;

// Layout is proven in the browser, not here (see AGENTS.md); these tests pin the
// contracts a phone layout depends on so a later edit cannot silently drop one.
// ConnectionDialogTests is grandfathered over the file-length ceiling.
[TestFixture]
public class ConnectionDialogPhoneLayoutTests : BunitTestContext
{
    private const string ComponentsCss = "src/MqttProbe.UI/wwwroot/css/components.css";
    private const string DialogCss = "src/MqttProbe.UI/Components/Browser/Connection/ConnectionDialog.razor.css";
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
    public void Footer_NeverShrinksBelowThePanel_AndTightensOnPhones()
    {
        ReadCssRule(DialogCss, ".connection-actions-footer").Should().Contain("flex-shrink: 0");
        ReadPhoneMediaBlockContaining(DialogCss, ".connection-actions-footer {")
            .Should().Contain("padding: 8px 12px");
    }

    [Test]
    public void StepSelector_LabelDoesNotWrapOnTheNarrowestPhone()
    {
        ReadCssRule(DialogCss, ".step-btn").Should().Contain("white-space: nowrap");
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
    public void ViewportScript_RevealsTheFocusedControl_AboveTheStickyFooter_NotJustInsideTheCard()
    {
        var script = ReadRepositoryFile(ViewportScript);
        var reveal = ExtractBracedBlockFromFirstBrace(
            script[script.IndexOf("const revealFocusedField = () => {", StringComparison.Ordinal)..]);

        // The footer is stuck over the bottom of the card in the fallback, so the band a
        // control may land in stops at the footer, and the opening transform is divided out
        // so the scroll amount stays in layout pixels.
        script.Should().Contain("const REVEAL_MARGIN = 8");
        script.Should().Contain("const scrollOwner = () =>");
        reveal.Should().Contain("Math.min(footer.getBoundingClientRect().top, scrollerBox.bottom)");
        reveal.Should().Contain("const tallerThanBand = fieldBox.height > visibleBottom - visibleTop");
        reveal.Should().Contain("scroller.scrollTop = Math.max(0, scroller.scrollTop + (delta / scale))");
        reveal.Should().Contain("paperBox.width / paper.offsetWidth");
    }

    [Test]
    public void ViewportScript_WatchesTheLayout_SoChromeThatGrowsWithoutAnEventIsStillMeasured()
    {
        var script = ReadRepositoryFile(ViewportScript);

        // A footer alert (a name collision, a failed connection) or a growing panel takes
        // room from the panel with no viewport or focus event behind it.
        script.Should().Contain("new ResizeObserver(schedule)");
        script.Should().Contain("observer?.observe(paper)");
        script.Should().Contain("observer?.observe(contentElement)");
        script.Should().Contain("observer?.observe(footer)");
        script.Should().Contain("observer?.disconnect()");
        script.Should().NotContain("SETTLE_FRAMES");
    }

    [Test]
    public void ViewportScript_MeasuresTheFitInLayoutSizes_NotPaintedRects()
    {
        var script = ReadRepositoryFile(ViewportScript);
        var fit = ExtractBracedBlockFromFirstBrace(
            script[script.IndexOf("const panelHeight = () => {", StringComparison.Ordinal)..]);

        // A transform (MudBlazor's opening animation) scales every rect, and the fallback
        // itself changes which element scrolls, so the fit reads the card's bounded box and
        // the chrome's layout heights instead.
        fit.Should().Contain("paper.clientHeight");
        fit.Should().Contain("element.offsetHeight");
        fit.Should().NotContain("getBoundingClientRect");
        fit.Should().Contain(".step-selector");
        fit.Should().Contain(".mud-dialog-title");
        fit.Should().Contain(".connection-dialog-header");
        fit.Should().Contain(".mud-tabs-tabbar");
        fit.Should().Contain(".connection-actions-footer");
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

        // One module is shared by every dialog, so a closing dialog must be able to
        // release its own registration and nothing else.
        script.Should().Contain("activeAttachments.set(attachment, teardown)");
        script.Should().Contain("activeAttachments.get(attachment)");
        script.Should().NotContain("activeAttachments.clear()",
            "detach must never sweep another dialog's attachment");
    }

    [Test]
    public void ViewportScript_MeasuresBothHiddenStripsOfTheVisualViewport()
    {
        var script = ReadRepositoryFile(ViewportScript);

        // The top strip is visualViewport.offsetTop; the bottom one is what is left
        // between the panned viewport and the bottom of the layout viewport.
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
    public void ViewportScript_CountsTheStepSelectorInTheChromeThePanelNeverGets()
    {
        var script = ReadRepositoryFile(ViewportScript);
        var chrome = ExtractBracedBlockFromFirstBrace(
            script[script.IndexOf("const panelHeight = () => {", StringComparison.Ordinal)..]);

        chrome.Should().Contain(".step-selector");
        chrome.Should().Contain(".mud-dialog-title");
        chrome.Should().Contain(".connection-dialog-header");
        chrome.Should().Contain(".mud-tabs-tabbar");
        chrome.Should().Contain(".connection-actions-footer");
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

        // The strips are inherited state, so they have to be published on the container the
        // band is spent on rather than on the card inside it.
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
    }

    [Test]
    public void ConnectionFields_WebSocketBasePath_UsesTheUrlKeyboard()
    {
        var cut = Render<ConnectionFields>(p =>
            p.Add(x => x.Connection, new Connection { Protocol = Protocol.WebSocket }));

        FindInputForLabel(cut, "WebSocket Base Path").GetAttribute("inputmode").Should().Be("url");
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
    public void CompactFallback_TheCardScrollsTheWholeForm_WithAStickyFooter()
    {
        var css = ReadRepositoryFile(DialogCss);

        var compactContent = ExtractBracedBlockFromFirstBrace(
            css[css.IndexOf(".connection-dialog-content--compact {", StringComparison.Ordinal)..]);
        compactContent.Should().Contain("overflow-y: auto");
        css.Should().Contain(".connection-dialog-content--compact .connection-actions-footer {");
        css.Should().Contain("position: sticky");

        var compactPanel = ExtractBracedBlockFromFirstBrace(
            css[css.IndexOf(
                ".connection-dialog-content--compact ::deep .connection-tabs .mud-tab-panel[role='tabpanel'].mud-tab-panel-active",
                StringComparison.Ordinal)..]);
        compactPanel.Should().Contain("padding-bottom: calc(16px + var(--connection-dialog-footer-height, 0px))");
        css.Should().Contain("::deep .connection-tabs .mud-tab-panel[role='tabpanel'].mud-tab-panel-active",
            "the panel stops being a second scroll owner in the fallback");

        var script = ReadRepositoryFile(ViewportScript);
        script.Should().Contain("MIN_USABLE_PANEL_HEIGHT");
        script.Should().Contain("container.classList.toggle(CONTAINER_KEYBOARD_CLASS, hidden)");
        script.Should().Contain("container.style.setProperty(FOOTER_HEIGHT_VARIABLE");
    }

    [Test]
    public void CompactFallback_PanelClipsBothAxes_SoTheCardIsTheOnlyScrollOwner()
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
            "a direct child of the scrolling card must not shrink away in the fallback");
    }

    [Test]
    public void Notices_HostIsAlwaysInTheMarkup_WithTheAlertsInsideIt()
    {
        var markup = ReadRepositoryFile(DialogMarkup);

        var host = markup.IndexOf("<div class=\"connection-dialog-notices\">", StringComparison.Ordinal);
        var collision = markup.IndexOf("@if (_nameCollisionError is not null)", StringComparison.Ordinal);
        var failure = markup.IndexOf("@if (_connectionState == ConnectionState.Failed)", StringComparison.Ordinal);
        var actions = markup.IndexOf("class=\"connection-actions-footer\"", StringComparison.Ordinal);

        // A host that only exists while an alert does would leave the layout observer with
        // nothing to watch, and the fit would stay where the shorter chrome put it.
        host.Should().BeGreaterThanOrEqualTo(0);
        collision.Should().BeGreaterThan(host, "the alerts belong inside the host");
        failure.Should().BeGreaterThan(host, "the alerts belong inside the host");
        collision.Should().BeLessThan(actions);
        failure.Should().BeLessThan(actions);
        markup[Math.Max(0, host - 120)..host].Should().NotContain("@if");
        markup[Math.Max(0, host - 120)..host].Should().NotContain("{");
    }

    [Test]
    public void CompactFooter_IsASiblingOfTheForm_SoTheCardIsItsStickyContainingBlock()
    {
        var markup = ReadRepositoryFile(DialogMarkup);

        var formClose = markup.IndexOf("</MudForm>", StringComparison.Ordinal);
        var contentOpen = markup.IndexOf("class=\"connection-dialog-content\"", StringComparison.Ordinal);
        var formOpen = markup.IndexOf("<MudForm", StringComparison.Ordinal);
        var footer = markup.IndexOf("class=\"connection-actions-footer\"", StringComparison.Ordinal);

        formOpen.Should().BeGreaterThanOrEqualTo(0);
        formClose.Should().BeGreaterThan(formOpen);
        contentOpen.Should().BeGreaterThanOrEqualTo(0);
        footer.Should().BeGreaterThan(formClose, "the footer must sit outside the form");

        var betweenFormAndFooter = markup[(formClose + "</MudForm>".Length)..footer];
        betweenFormAndFooter.Should().NotContain("<MudForm",
            "the footer must sit outside the form");
        (betweenFormAndFooter.Split("</div>").Length - 1).Should().BeLessThanOrEqualTo(1,
            "the footer must be a sibling of the form and of the notices, not nested inside either");
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
        // Media queries can share a prefix — "(max-width: 599px)" also opens the
        // capability query — so the header has to be matched whole.
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
