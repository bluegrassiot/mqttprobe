// iOS keeps the layout viewport at full height while the keyboard is open, so the strips its visual viewport leaves are measured and published as custom properties.

const BOTTOM_INSET_VARIABLE = '--connection-dialog-keyboard-inset';
const TOP_INSET_VARIABLE = '--connection-dialog-viewport-top-inset';
const NARROW_MEDIA_QUERY = '(max-width: 599px)';
const COARSE_POINTER_MEDIA_QUERY = '(pointer: coarse)';

const COMPACT_CLASS = 'connection-dialog-content--compact';
const KEYBOARD_CLASS = 'connection-dialog-content--keyboard';
const CONTAINER_KEYBOARD_CLASS = 'connection-dialog-container--keyboard';
const SHELL_KEYBOARD_CLASS = 'app-keyboard-open';
const SHELL_HEIGHT_VARIABLE = '--app-keyboard-height';
const SHELL_TOP_VARIABLE = '--app-keyboard-top-inset';
const SHELL_CHANGE_EVENT = 'app-keyboard-viewport-change';
const SHELL_RECOVERY_ATTRIBUTE = 'data-app-keyboard-viewport-recovery';

const MIN_USABLE_PANEL_HEIGHT = 120;
const MIN_KEYBOARD_OCCLUSION = 120;
const REVEAL_MARGIN = 8;
const KEYBOARD_RELEASE_DELAY = 250;

const activeAttachments = new Map();
let nextAttachment = 1;

// Focus memory is per dialog content element, so a confirmation can hand focus back to the
// control the user was editing without a second focus trap.
const focusMemory = new WeakMap();

export function rememberFocus(contentElement) {
    const active = document.activeElement;
    if (contentElement && active instanceof HTMLElement && contentElement.contains(active)) {
        focusMemory.set(contentElement, active);
    }
}

export function restoreFocus(contentElement) {
    if (!contentElement) {
        return false;
    }

    const previous = focusMemory.get(contentElement);
    focusMemory.delete(contentElement);

    if (previous && previous.isConnected && contentElement.contains(previous)) {
        previous.focus();
        if (document.activeElement === previous) {
            return true;
        }
    }

    contentElement.focus();
    return false;
}

export function attach(contentElement) {
    if (!contentElement) {
        return null;
    }

    const paper = contentElement.closest('.mud-dialog') ?? contentElement;
    // Custom properties inherit downwards, so the strips are published on the container the band is spent on.
    const container = paper.closest('.mud-dialog-container') ?? paper;
    const viewport = window.visualViewport;
    const narrow = window.matchMedia(NARROW_MEDIA_QUERY);
    const coarsePointer = window.matchMedia(COARSE_POINTER_MEDIA_QUERY);
    let frameHandle = 0;

    const keyboardCanAppear = () => narrow.matches || coarsePointer.matches;

    const hasFocusedEditor = () => {
        const active = document.activeElement;
        return active instanceof HTMLElement
            && contentElement.contains(active)
            && active.matches('input, textarea, select, [contenteditable="true"]');
    };

    // The keyboard takes height off the visual viewport whatever the zoom level and
    // wherever the viewport is panned; the pan and the keyboard gap are then read
    // from the raw values to place the band. Measuring coverage with the pan folded
    // in would let a panned viewport cancel the keyboard out.
    const measureHiddenStrips = () => {
        // A zero or non-finite visual viewport is a broken frame, not a full-height keyboard.
        if (!viewport
            || !isFinite(viewport.height)
            || viewport.height <= 0
            || !isFinite(window.innerHeight)
            || window.innerHeight <= 0) {
            return { top: 0, bottom: 0 };
        }

        const scale = viewport.scale > 1 ? viewport.scale : 1;
        const covered = window.innerHeight / scale - viewport.height;
        if (covered <= MIN_KEYBOARD_OCCLUSION) {
            return { top: 0, bottom: 0 };
        }

        return {
            top: Math.max(0, Math.round(viewport.offsetTop)),
            bottom: Math.max(0, Math.round(
                window.innerHeight - (viewport.height + viewport.offsetTop))),
        };
    };

    let recoveryFrameHeight = 0;

    const rawScaleIsOne = () => {
        const raw = viewport ? viewport.scale : 1;
        return typeof raw === 'number' && isFinite(raw) && Math.abs(raw - 1) <= 0.01;
    };

    // The same narrow pan context the shell corrects: coarse, unzoomed, focused dialog
    // editor, fitting root and a real root pan. Retained only through the settle window.
    const recoveryFrame = () => {
        const root = document.documentElement;
        if (root.getAttribute(SHELL_RECOVERY_ATTRIBUTE) !== 'settling') {
            recoveryFrameHeight = 0;
            return 0;
        }

        const client = root.clientHeight;
        const panned = coarsePointer.matches
            && rawScaleIsOne()
            && root.classList.contains(SHELL_KEYBOARD_CLASS)
            && hasFocusedEditor()
            && isFinite(client) && client > 0
            && root.scrollHeight <= client + 2
            && client > window.innerHeight + 1;
        recoveryFrameHeight = panned ? client : 0;
        return recoveryFrameHeight;
    };

    // The container frame is the root box while a correction is settling, otherwise the
    // window height as before.
    const layoutFrame = () => recoveryFrame() || window.innerHeight;

    // The shell publishes the band it already selected; this script must not re-derive
    // one from a raw visual viewport that reported zero while the keyboard is up.
    const readShellBand = () => {
        if (!document.documentElement.classList.contains(SHELL_KEYBOARD_CLASS)) {
            return null;
        }

        const rootStyle = document.documentElement.style;
        const top = parseFloat(rootStyle.getPropertyValue(SHELL_TOP_VARIABLE));
        const height = parseFloat(rootStyle.getPropertyValue(SHELL_HEIGHT_VARIABLE));
        const layoutHeight = layoutFrame();
        if (!isFinite(top) || !isFinite(height) || top < 0 || height <= 0
            || !isFinite(layoutHeight) || layoutHeight <= 0) {
            return null;
        }

        const boundedTop = Math.min(top, layoutHeight);
        return {
            top: Math.round(boundedTop),
            bottom: Math.max(0, Math.round(layoutHeight - (boundedTop + height))),
        };
    };

    const clearInsets = () => {
        container.style.removeProperty(TOP_INSET_VARIABLE);
        container.style.removeProperty(BOTTOM_INSET_VARIABLE);
    };

    // client/offset sizes are layout values, so MudBlazor's opening transform cannot scale them.
    const panelHeight = () => {
        const paperStyle = getComputedStyle(paper);
        const wrapper = paper.querySelector('.mud-dialog-content');
        const wrapperStyle = wrapper ? getComputedStyle(wrapper) : null;
        const wrapperPadding = wrapperStyle
            ? (parseFloat(wrapperStyle.paddingTop) || 0) + (parseFloat(wrapperStyle.paddingBottom) || 0)
            : 0;
        const chrome = [
            paper.querySelector('.mud-dialog-title'),
            contentElement.querySelector('.connection-dialog-titlebar'),
            contentElement.querySelector('.connection-dialog-header'),
            contentElement.querySelector('.step-selector'),
            contentElement.querySelector('.mud-tabs-tabbar'),
            contentElement.querySelector('.connection-dialog-notices'),
        ].reduce((total, element) => total + (element ? element.offsetHeight : 0), 0);

        return paper.clientHeight
            - (parseFloat(paperStyle.paddingTop) || 0)
            - (parseFloat(paperStyle.paddingBottom) || 0)
            - wrapperPadding
            - chrome;
    };

    const isTextControl = (element) =>
        element instanceof HTMLElement
        && element.matches('input, textarea, select, [contenteditable="true"]');

    // Mode picks the scroll owner, so a transient overflow reading cannot flip it.
    const scrollOwner = () => {
        const form = contentElement.querySelector('.mud-form');
        const panel = contentElement.querySelector('.mud-tab-panel-active');
        const overflows = (element) => element && element.scrollHeight > element.clientHeight + 1;

        if (contentElement.classList.contains(COMPACT_CLASS)) {
            return overflows(form) ? form : overflows(panel) ? panel : form ?? panel;
        }

        return overflows(panel) ? panel : overflows(form) ? form : null;
    };

    const blurHiddenFocus = () => {
        const active = document.activeElement;
        if (!(active instanceof HTMLElement) || !contentElement.contains(active) || !isTextControl(active)) {
            return;
        }

        const rect = active.getBoundingClientRect();
        if (active.offsetParent === null || rect.width === 0 || rect.height === 0) {
            active.blur();
        }
    };

    // The scroll owner's box ends the band a control may land in.
    const revealFocusedField = (strips) => {
        const active = document.activeElement;
        if (!active || !contentElement.contains(active) || !isTextControl(active)) {
            return;
        }

        const scroller = scrollOwner();
        if (!scroller) {
            return;
        }

        // The opening scale multiplies every rect, so divide it back out to keep the scroll in layout pixels.
        const paperBox = paper.getBoundingClientRect();
        const scale = paper.offsetWidth > 0 ? paperBox.width / paper.offsetWidth : 1;
        const scrollerBox = scroller.getBoundingClientRect();
        const fieldBox = active.getBoundingClientRect();
        // While a whole-document pan is still in progress the window height is not the
        // band frame, and scroll placement would amplify the pan.
        recoveryFrame();
        if (recoveryFrameHeight > 0) {
            return;
        }

        const viewportTop = strips.top;
        const viewportBottom = layoutFrame() - strips.bottom;

        const bandTop = Math.max(scrollerBox.top, viewportTop);
        const bandBottom = Math.min(scrollerBox.bottom, viewportBottom);

        // The margins give way when the band is only just tall enough for the control.
        const margin = Math.min(REVEAL_MARGIN, Math.max(0, bandBottom - bandTop - fieldBox.height) / 2);
        const visibleTop = bandTop + margin;
        const visibleBottom = bandBottom - margin;

        if (fieldBox.top >= visibleTop && fieldBox.bottom <= visibleBottom) {
            return;
        }

        // A control taller than the band shows the most of itself flush to the band's top.
        const tallerThanBand = fieldBox.height > visibleBottom - visibleTop;
        const delta = tallerThanBand || fieldBox.top < visibleTop
            ? fieldBox.top - visibleTop
            : fieldBox.bottom - visibleBottom;
        scroller.scrollTop = Math.max(0, scroller.scrollTop + (delta / scale));
    };

    const setCompact = (compact) => {
        contentElement.classList.toggle(COMPACT_CLASS, compact);
    };

    const setKeyboard = (keyboard) => {
        contentElement.classList.toggle(KEYBOARD_CLASS, keyboard);
    };

    let keyboardActive = false;
    let releaseTimer = 0;
    let shellSeen = false;

    const publishBand = (strips, keyboard) => {
        if (keyboardCanAppear()) {
            container.style.setProperty(TOP_INSET_VARIABLE, `${strips.top}px`);
            container.style.setProperty(BOTTOM_INSET_VARIABLE, `${strips.bottom}px`);
        } else {
            clearInsets();
        }

        // The container carries the band, not the card: a card out of flow paints under the backdrop and swallows taps.
        container.classList.toggle(CONTAINER_KEYBOARD_CLASS, keyboard);

        const compact = panelHeight() < MIN_USABLE_PANEL_HEIGHT;
        setCompact(compact);
        setKeyboard(keyboard);
        blurHiddenFocus();

        if (keyboard || compact) {
            revealFocusedField(strips);
        }
    };

    const releaseBand = () => {
        releaseTimer = 0;
        keyboardActive = false;
        publishBand({ top: 0, bottom: 0 }, false);
    };

    const apply = () => {
        const eligible = keyboardCanAppear();
        const shellClass = document.documentElement.classList.contains(SHELL_KEYBOARD_CLASS);
        // Once the shell has published, its absence is a closed keyboard; the raw
        // viewport is only a bootstrap source before the shell script has run.
        if (shellClass) {
            shellSeen = true;
        }

        const shellBand = eligible ? readShellBand() : null;
        const strips = !eligible
            ? { top: 0, bottom: 0 }
            : shellBand ?? (shellSeen ? { top: 0, bottom: 0 } : measureHiddenStrips());
        const hidden = eligible && (strips.top > 0 || strips.bottom > 0);
        // A webview can hand the keyboard to the page by shrinking its native view, which
        // leaves no visual viewport strip to measure. The shell already detects that and
        // publishes it, so the dialog hides its chrome on the same evidence. Placement
        // stays on the measured strips, which are zero there, so no inset is added twice.
        const shellKeyboard = eligible
            && hasFocusedEditor()
            && document.documentElement.classList.contains(SHELL_KEYBOARD_CLASS);
        const keyboard = hidden || shellKeyboard;

        if (keyboard) {
            if (releaseTimer) {
                window.clearTimeout(releaseTimer);
                releaseTimer = 0;
            }

            keyboardActive = true;
            publishBand(strips, true);
            return;
        }

        // The band can end while a finger is on a control in it: leaving an editor
        // releases it, and a zoomed visual viewport without focus no longer counts as
        // a keyboard. Resizing under that tap would drop its click, so the release
        // waits for the gesture to finish.
        if (keyboardActive) {
            if (!releaseTimer) {
                releaseTimer = window.setTimeout(releaseBand, KEYBOARD_RELEASE_DELAY);
            }

            return;
        }

        publishBand(strips, false);
    };

    const schedule = () => {
        if (frameHandle) {
            return;
        }

        frameHandle = window.requestAnimationFrame(() => {
            frameHandle = 0;
            apply();
        });
    };

    const blurAfterTabClick = () => {
        window.requestAnimationFrame(blurHiddenFocus);
    };

    // An alert or a growing panel resizes the chrome with no viewport event; apply() only toggles classes, so it cannot feed back.
    const observer = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(schedule);
    observer?.observe(paper);
    observer?.observe(contentElement);

    // The shell owns the keyboard signal, so react to it directly rather than depending
    // on which listener asked for the next animation frame first.
    const shellObserver = typeof MutationObserver === 'undefined'
        ? null
        : new MutationObserver(schedule);
    shellObserver?.observe(document.documentElement, { attributes: true, attributeFilter: ['class'] });

    const notices = contentElement.querySelector('.connection-dialog-notices');
    if (notices) {
        observer?.observe(notices);
    }

    const teardown = () => {
        if (frameHandle) {
            window.cancelAnimationFrame(frameHandle);
            frameHandle = 0;
        }

        observer?.disconnect();
        shellObserver?.disconnect();
        window.clearTimeout(releaseTimer);
        releaseTimer = 0;
        keyboardActive = false;
        viewport?.removeEventListener('resize', schedule);
        viewport?.removeEventListener('scroll', schedule);
        window.removeEventListener('resize', schedule);
        window.removeEventListener('orientationchange', schedule);
        window.removeEventListener(SHELL_CHANGE_EVENT, schedule);
        narrow.removeEventListener('change', schedule);
        coarsePointer.removeEventListener('change', schedule);
        contentElement.removeEventListener('focusin', schedule);
        contentElement.removeEventListener('click', blurAfterTabClick, true);
        clearInsets();
        container.classList.remove(CONTAINER_KEYBOARD_CLASS);
        setKeyboard(false);
        setCompact(false);
    };

    viewport?.addEventListener('resize', schedule);
    viewport?.addEventListener('scroll', schedule);
    window.addEventListener('resize', schedule);
    window.addEventListener('orientationchange', schedule);
    window.addEventListener(SHELL_CHANGE_EVENT, schedule);
    narrow.addEventListener('change', schedule);
    coarsePointer.addEventListener('change', schedule);
    contentElement.addEventListener('focusin', schedule);
    contentElement.addEventListener('click', blurAfterTabClick, true);

    const attachment = nextAttachment++;
    activeAttachments.set(attachment, teardown);
    apply();

    return attachment;
}

export function detach(attachment) {
    const teardown = activeAttachments.get(attachment);
    if (!teardown) {
        return;
    }

    activeAttachments.delete(attachment);
    teardown();
}
