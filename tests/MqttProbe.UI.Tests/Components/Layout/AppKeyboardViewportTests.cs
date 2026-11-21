namespace MqttProbe.UI.Tests.Components.Layout;

[TestFixture]
public class AppKeyboardViewportTests
{
    private const string ComponentsCss = "src/MqttProbe.UI/wwwroot/css/components.css";
    private const string ViewportScript = "src/MqttProbe.UI/wwwroot/js/app-keyboard-viewport.js";
    private const string DialogViewportScript = "src/MqttProbe.UI/wwwroot/js/connection-dialog-viewport.js";

    [Test]
    public void Occlusion_IsMeasureAgainstTheZoomedViewport_NotTheRawHeight()
    {
        var script = ReadRepositoryFile(ViewportScript);

        script.Should().Contain("const scale = viewport && viewport.scale > 1 ? viewport.scale : 1;");
        script.Should().Contain("const unobstructedHeight = validLayout ? window.innerHeight / scale : 0;");
        script.Should().Contain("const occluded = vvValid ? Math.max(0, unobstructedHeight - viewport.height) : 0;");
        script.Should().NotContain("unobstructedHeight - (viewport.height + viewport.offsetTop)",
            "where the viewport is panned does not change how much height a keyboard takes");
        script.Should().NotContain("window.innerHeight - (viewport.height + viewport.offsetTop)",
            "a zoomed visual viewport shrinks without a keyboard, so the raw difference reads a pinch as typing");
    }

    [Test]
    public void WidthChange_InvalidatesOnlyTheWidthSpecificRestingRoot()
    {
        var script = ReadRepositoryFile(ViewportScript);
        var branch = ExtractBlock(
            script,
            "if (Math.abs(window.innerWidth - lastWidth) > 1) {");

        branch.Should().Contain("lastWidth = window.innerWidth;");
        branch.Should().Contain("restRoot = null;");
        branch.Should().NotContain("clear()",
            "a fold with the keyboard up is not a dismissal");
        branch.Should().NotContain("keyboardEpisode = false;",
            "the keyboard episode outlives the rotation");
        branch.Should().NotContain("episodeRestHeight = 0;");
        branch.Should().NotContain("idleHeight = window.innerHeight;",
            "the keyboard-shrunken height must never become the resting baseline");
        script.Should().Contain("window.addEventListener('resize', schedule);",
            "a resize handler that pre-sets the width baseline bypasses the invalidation");
    }

    [Test]
    public void Occlusion_CountsANativeViewThatShrunkForTheKeyboard()
    {
        var script = ReadRepositoryFile(ViewportScript);

        script.Should().Contain("let idleHeight = window.innerHeight;");
        script.Should().Contain("Math.max(0, idleHeight - window.innerHeight)");
        script.Should().Contain("const hiddenBottom = Math.max(occluded, shrunk);");
        script.Should().Contain("idleHeight = window.innerHeight;",
            "the resting height is refreshed when the episode ends, not on every blur");
    }

    [Test]
    public void Reveal_BoundsBothEdgesOfTheBand_WhenTheViewportIsPanned()
    {
        var script = ReadRepositoryFile(ViewportScript);

        script.Should().Contain("const viewportTop = band.top;");
        script.Should().Contain("const visibleTop = Math.max(scrollerBand.top, viewportTop) + REVEAL_MARGIN;");
        script.Should().Contain("const visibleBottom = Math.min(scrollerBand.bottom, viewportBottom) - REVEAL_MARGIN;");
        script.Should().NotContain("field.top < band.top + 8",
            "a panned viewport hides the top of the scroller, so the raw scroller top is not the visible edge");
    }

    [Test]
    public void KeyboardMode_HidesTheChromeThatWouldOtherwiseSitAboveTheKeys()
    {
        var css = ReadRepositoryFile(ComponentsCss);

        css.Should().Contain("html.app-keyboard-open .mobile-primary-nav {");
        css.Should().Contain("html.app-keyboard-open .app-shell-appbar {");
        css.Should().Contain("html.app-keyboard-open:not(.app-keyboard-editor-in-header) .app-tabpanel-header");
        css.Should().Contain("html.app-keyboard-open .browser-mobile-payload {");
        css.Should().Contain("--app-mobile-nav-reserve: calc(68px + env(safe-area-inset-bottom, 0px));",
            "the reserve follows the shell's safe-area-aware token rather than a fixed height");

        ExtractBlock(css, "html.app-keyboard-open .browser-mobile-payload {")
            .Should().Contain("padding-bottom: 0",
                "the nav is gone while typing, so its reserved padding goes with it");
    }

    [Test]
    public void BothViewportScripts_MeasureKeyboardCoverageTheSameWay()
    {
        var dialogScript = ReadRepositoryFile(DialogViewportScript);

        ReadRepositoryFile(ViewportScript).Should().Contain("validLayout ? window.innerHeight / scale : 0;");
        ReadRepositoryFile(ViewportScript).Should().Contain("Math.max(0, unobstructedHeight - viewport.height)");
        dialogScript.Should().Contain("const covered = window.innerHeight / scale - viewport.height;");
        ReadRepositoryFile(ViewportScript).Should().Contain("const MIN_KEYBOARD_OCCLUSION = 120;");
        dialogScript.Should().Contain("const MIN_KEYBOARD_OCCLUSION = 120;",
            "the dialog and the shell must agree on what counts as a keyboard");
    }

    [Test]
    public void DialogScript_TakesTheShellKeyboardSignal_WithoutAddingAnInset()
    {
        var script = ReadRepositoryFile(DialogViewportScript);

        script.Should().Contain("const SHELL_KEYBOARD_CLASS = 'app-keyboard-open';");
        script.Should().Contain("document.documentElement.classList.contains(SHELL_KEYBOARD_CLASS)");
        script.Should().Contain("const keyboard = hidden || shellKeyboard;");
        script.Should().Contain("publishBand(strips, true)");
        script.Should().Contain("attributeFilter: ['class']",
            "the dialog has to react to the shell class directly, not to frame ordering");
        script.Should().Contain("shellObserver?.disconnect()");
    }

    // Shape only. The transition itself is covered by the external Playwright probes;
    // keep this to the invariants that would silently regress the guard.
    [Test]
    public void RootPanCorrection_ResetsTheRootPanOnceAndDefersPublishing()
    {
        var script = ReadRepositoryFile(ViewportScript);

        script.Should().Contain("const SCROLL_RESET_TOLERANCE = 2;");
        script.Should().Contain("const SCROLL_SETTLE_MS = 120;");
        script.Should().Contain("window.scrollTo(window.scrollX, 0);");
        script.Should().Contain("window.scrollY > SCROLL_RESET_TOLERANCE");
        script.Should().Contain("root.scrollHeight <= root.clientHeight + SCROLL_RESET_TOLERANCE");
        script.Should().Contain("active.closest('.connection-dialog-content') !== null",
            "the correction is scoped to the demonstrated surface");
        script.Should().Contain("Math.abs(raw - 1) <= 0.01",
            "the guard uses the raw scale, so NaN and 0.5 cannot normalize into a correction");
        script.Should().Contain("scrollCorrectionUsed = true;");
        script.Should().Contain("scrollCorrectionUsed = false;");
        script.Should().Contain("window.addEventListener('scroll', schedule");
        script.Should().Contain("const canCorrectPannedRoot = () => !posturePending && strictPanContext();",
            "recovery is a narrow guard, not a general scroll correction");
        script.Should().Contain("root.scrollHeight <= root.clientHeight + SCROLL_RESET_TOLERANCE",
            "only a fitting current root may be corrected");
        script.Should().Contain("window.scrollY > SCROLL_RESET_TOLERANCE");
        script.Should().Contain("correctedWidth = window.innerWidth;",
            "the attempt is tied to the posture it was spent on");
        script.Should().Contain("if (generation !== postureGeneration) {",
            "a stale quiet callback must not grant an attempt");
        script.Should().Contain("posturePending = true;",
            "even a fresh budget waits for the posture to settle");
        script.Should().Contain("data-app-keyboard-viewport-recovery",
            "the shell publishes recovery metadata the dialog can read");
        script.Should().Contain("postureTimer = window.setTimeout(",
            "a width burst re-arms only once it settles");

        var settle = script.IndexOf("if (scrollSettleAt) {", StringComparison.Ordinal);
        var toggle = script.IndexOf("classList.toggle(KEYBOARD_CLASS", StringComparison.Ordinal);
        settle.Should().BeGreaterThan(0);
        settle.Should().BeLessThan(toggle, "the settling pass has to return before publishing geometry");
    }

    // Shape only. The eligibility itself is proven by the external Playwright probes.
    [Test]
    public void RestingRootFallback_OnlyTrustsARememberedRestingRoot()
    {
        var script = ReadRepositoryFile(ViewportScript);

        script.Should().Contain("const MIN_REST_HEIGHT_DROP = MIN_KEYBOARD_OCCLUSION;");
        script.Should().Contain("{ width: window.innerWidth, clientHeight: root.clientHeight }");
        script.Should().Contain("restRoot = null;");
        script.Should().Contain("Math.abs(window.innerWidth - restRoot.width) > 1",
            "a baseline from another width or orientation must not be reused");
        script.Should().Contain("restRoot.clientHeight - root.clientHeight <= MIN_REST_HEIGHT_DROP",
            "the native view has to have given up more than the keyboard threshold");
        script.Should().Contain("root.scrollHeight <= restRoot.clientHeight + SCROLL_RESET_TOLERANCE",
            "content taller than the remembered resting height is a real overflow");

        var capture = ExtractBlock(script, "const rememberRestRoot = () => {");
        capture.Should().Contain("root.scrollHeight <= root.clientHeight + SCROLL_RESET_TOLERANCE");
        capture.Should().Contain("window.scrollY <= SCROLL_RESET_TOLERANCE");
        capture.Should().Contain(": null;", "a panned or overflowing rest page has no usable baseline");
        capture.Should().Contain("window.innerHeight < idleHeight - SCROLL_RESET_TOLERANCE",
            "a native shrink in progress must not clear the last good resting baseline");

        var widthBranch = ExtractBlock(script, "if (Math.abs(window.innerWidth - lastWidth) > 1) {");
        widthBranch.Should().Contain("restRoot = null;");
        widthBranch.Should().NotContain("keyboardEpisode = false;");
    }

    [Test]
    public void RestingRootFallback_IsFrozenUntilCoverageAndHeightAreRestored()
    {
        var script = ReadRepositoryFile(ViewportScript);

        script.Should().Contain("let keyboardEpisode = false;");
        script.Should().Contain("let episodeRestHeight = 0;");
        script.Should().Contain("const shrunk = coarsePointer.matches",
            "the shrink is geometry, so a blurred but still short view keeps the keyboard signal");
        script.Should().NotContain("const shrunk = editing &&",
            "blur alone must not drop the geometry the episode is established on");
        script.Should().NotContain("if (!editing) {",
            "blur must not overwrite the resting height while the view is still short");

        var started = ExtractBlock(script, "if (!keyboardEpisode) {");
        started.Should().Contain("keyboardEpisode = true;");
        started.Should().Contain("episodeRestHeight = restRoot ? restRoot.clientHeight : idleHeight;",
            "with no eligible resting root the preserved idle height is the restoration target");

        var released = ExtractBlock(script, "if (keyboardEpisode) {");
        released.Should().NotContain("editing", "the end is not focus-driven");
        released.Should().Contain("keyboardEpisode = false;");
        released.Should().Contain("scrollCorrectionUsed = false;",
            "a spent correction stays spent through blur and refocus");
        released.Should().Contain("episodeRestHeight = 0;");
        released.Should().Contain("lastBand = null;");

        var settlement = ExtractBlock(script, "const measured = measureBand();");
        settlement.Should().Contain("const layoutFull = !!measured");
        settlement.Should().Contain("measured.height >= Math.round(layoutHeight / scale) - BAND_TOLERANCE",
            "settlement needs a valid full viewport for the current layout");
        settlement.Should().Contain("let settledFull = validLayout && !!measured",
            "a broken measurement must not be accepted as dismissal");
        script.Should().Contain("hiddenBottom <= MIN_KEYBOARD_OCCLUSION",
            "the episode ends only when no geometric keyboard coverage remains");
        script.Should().NotContain("layoutHeight >= restReference",
            "a shorter restored layout must still settle, so the old rest height cannot gate the end");

        var checkpoint = ExtractBlock(script, "if (!settledFull && (keyboardEpisode || lastBand)) {");
        checkpoint.Should().Contain("recoveryBand = clampBand(lastBand, layoutHeight) ?? recoveryBand;",
            "a WillHide during a display handoff keeps a bounded recovery band");
        checkpoint.Should().Contain("recoveryWidth = window.innerWidth;");
        script.Should().Contain("recoveryBand = null;",
            "restored full geometry commits the end instead of keeping a recovery");
        script.Should().Contain("if (settledFull) {");
        script.Should().Contain("idleHeight = layoutHeight;",
            "the resting height is only adopted from restored full geometry");
        script.Should().Contain("native === 'unknown' && !keyboardEpisode && recoveryBand");
        script.Should().Contain("window.innerWidth !== recoveryWidth",
            "the reopen is recognised by a new posture, not by endless unknown frames");

        var firstGuard = script.IndexOf("if (!keyboardEpisode) {", StringComparison.Ordinal);
        var secondGuard = script.IndexOf("if (!keyboardEpisode) {", firstGuard + 1, StringComparison.Ordinal);
        secondGuard.Should().BeGreaterThan(firstGuard);
        ExtractBlock(script[secondGuard..], "if (!keyboardEpisode) {")
            .Should().Contain("rememberRestRoot();",
                "a blurred but still short view must not replace the resting baseline");

        var widthBranch = ExtractBlock(script, "if (Math.abs(window.innerWidth - lastWidth) > 1) {");
        widthBranch.Should().Contain("restRoot = null;");
        widthBranch.Should().NotContain("keyboardEpisode = false;");
        widthBranch.Should().NotContain("episodeRestHeight = 0;");
        widthBranch.Should().Contain("if (generation !== postureGeneration) {",
            "only the settled posture's callback may re-arm the budget");

        var orientation = ExtractBlock(script, "window.addEventListener('orientationchange', () => {");
        orientation.Should().Contain("restRoot = null;");
        orientation.Should().NotContain("keyboardEpisode = false;",
            "an orientation change must not end a live keyboard episode");
        orientation.Should().NotContain("clear()");
    }

    [Test]
    public void MessageSearch_KeepsOneLinePerMatch_InAKeyboardBand()
    {
        var css = ReadRepositoryFile(ComponentsCss);

        css.Should().Contain("html.app-keyboard-open .payload-browser-panel:has(input:focus) .payload-list-header");
        css.Should().Contain("html.app-keyboard-open .payload-browser-panel:has(input:focus) .pb-cell-payload");
        css.Should().Contain("html.app-keyboard-open .payload-browser-panel:has(input:focus) .payload-row {");
        css.Should().Contain("flex-wrap: nowrap", "the shortened row has to stay on one line to leave the field room");
    }

    [Test]
    public void NativeKeyboardState_IsReadFromTheRootAttributeAndItsEvent()
    {
        var script = ReadRepositoryFile(ViewportScript);

        script.Should().Contain("const NATIVE_STATE_ATTRIBUTE = 'data-app-native-keyboard-state';");
        script.Should().Contain("const NATIVE_CHANGE_EVENT = 'app-native-keyboard-change';");
        script.Should().Contain("const VIEWPORT_CHANGE_EVENT = 'app-keyboard-viewport-change';");
        script.Should().Contain("document.documentElement.getAttribute(NATIVE_STATE_ATTRIBUTE)");
        script.Should().Contain("window.addEventListener(NATIVE_CHANGE_EVENT, scheduleFromNative);",
            "a native open after the script loaded still schedules a pass");
        script.Should().Contain("document.addEventListener(NATIVE_CHANGE_EVENT, scheduleFromNative);",
            "a custom event dispatched on the document does not bubble to window");
        script.Should().Contain("const TOP_INSET_VARIABLE = '--app-keyboard-top-inset';");
    }

    [Test]
    public void NativeOpen_IsPresenceNotAHeight_AndUnknownIsNotADismissal()
    {
        var script = ReadRepositoryFile(ViewportScript);

        script.Should().Contain("keyboard = true;",
            "native open means a keyboard is present even at full viewport height");
        script.Should().Contain("keyboard = keyboardEpisode || (geometric && !dismissedByNative);",
            "an ambiguous native frame preserves an established episode but cannot revive a dismissed one");
        script.Should().Contain("keyboard = false;");
        script.Should().Contain("if (keyboardEpisode && native !== 'open' && settledFull) {");
        script.Should().Contain("dismissedByNative = true;");
        script.Should().Contain("dismissedByNative = false;",
            "only a real open or a recovered episode clears the dismissal");
    }

    [Test]
    public void BandMeasurement_RejectsOversizedAndOutOfBoundsReports()
    {
        var script = ReadRepositoryFile(ViewportScript);
        var measure = ExtractBlock(script, "const measureBand = (recovering) => {");

        script.Should().Contain("const BAND_TOLERANCE = 1;");
        measure.Should().Contain("const ceiling = layoutHeight / scale;");
        measure.Should().Contain("top >= 0 && height > 0");
        measure.Should().Contain("top + height <= ceiling + BAND_TOLERANCE",
            "an oversized frame is rejected before it can become the retained band");
        measure.Should().NotContain("Math.max(0, rawTop)",
            "a negative offset is a broken measurement, not a pan to clamp");

        var resolve = ExtractBlock(script, "const resolveBand = (measured, layoutHeight) => {");
        resolve.Should().Contain("if (measured) {");
        resolve.Should().Contain("lastBand = measured;");
    }

    [Test]
    public void FocusReveal_UsesTheSelectedBand_NotTheRawViewport()
    {
        var app = ReadRepositoryFile(ViewportScript);
        var dialog = ReadRepositoryFile(DialogViewportScript);

        app.Should().Contain("const revealFocusedEditor = (band) => {");
        app.Should().Contain("const viewportBottom = band.top + band.height;");
        app.Should().Contain("requestAnimationFrame(() => revealFocusedEditor(band))");
        app.Should().NotContain("const viewportBottom = viewport ? viewport.offsetTop + viewport.height : window.innerHeight;");

        dialog.Should().Contain("const revealFocusedField = (strips) => {");
        dialog.Should().Contain("const viewportBottom = layoutFrame() - strips.bottom;");
        dialog.Should().Contain("revealFocusedField(strips);");
    }

    [Test]
    public void Band_PublishesOnlyValidatedPositiveGeometry_AndRetainsTheLastBand()
    {
        var script = ReadRepositoryFile(ViewportScript);

        script.Should().Contain("const isPositive = (value) => typeof value === 'number' && isFinite(value) && value > 0;");
        script.Should().Contain("let lastBand = null;");
        script.Should().Contain("const retained = clampBand(lastBand, layoutHeight);");
        script.Should().Contain("return isPositive(layoutHeight) ? { top: 0, height: Math.round(layoutHeight) } : null;",
            "with no measurement yet the current positive view height is the band");
        script.Should().Contain("const band = resolveBand(measureBand(recoveryScope), validLayout ? bandBound : 0);");
        script.Should().Contain("document.documentElement.style.setProperty(HEIGHT_VARIABLE, `${band.height}px`);");
        script.Should().Contain("document.documentElement.style.setProperty(TOP_INSET_VARIABLE, `${band.top}px`);");
        script.Should().Contain("if (band) {",
            "an unmeasurable layout must not publish or delete a band");
    }

    [Test]
    public void DialogScript_PrefersTheShellBand_AndOnlyFallsBackBeforeTheShellHasPublished()
    {
        var script = ReadRepositoryFile(DialogViewportScript);

        script.Should().Contain("const SHELL_HEIGHT_VARIABLE = '--app-keyboard-height';");
        script.Should().Contain("const SHELL_TOP_VARIABLE = '--app-keyboard-top-inset';");
        script.Should().Contain("const SHELL_CHANGE_EVENT = 'app-keyboard-viewport-change';");
        script.Should().Contain("const shellBand = eligible ? readShellBand() : null;");
        script.Should().Contain("shellBand ?? (shellSeen ? { top: 0, bottom: 0 } : measureHiddenStrips())",
            "the raw viewport is bootstrap-only once the shell has published");
        script.Should().Contain("|| !isFinite(viewport.height)");
        script.Should().Contain("window.addEventListener(SHELL_CHANGE_EVENT, schedule);");
        script.Should().Contain("window.removeEventListener(SHELL_CHANGE_EVENT, schedule);");
    }

    private static string ExtractBlock(string text, string marker)
    {
        var start = text.IndexOf(marker, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"expected '{marker}' in the script");

        var depth = 0;
        for (var i = text.IndexOf('{', start); i < text.Length; i++)
        {
            if (text[i] == '{')
                depth++;
            else if (text[i] == '}' && --depth == 0)
                return text[start..(i + 1)];
        }

        throw new InvalidOperationException($"'{marker}' block is not closed");
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath));

        File.Exists(path).Should().BeTrue($"expected the repository file at '{path}' to exist");
        return File.ReadAllText(path);
    }
}
