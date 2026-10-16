// Keyboard-aware framing for the Setup Connection dialog.
//
// iOS keeps the layout viewport at full height while the software keyboard is open, so
// neither dvh nor env(safe-area-inset-bottom) can size the dialog against what is
// actually visible. The strips its visual viewport leaves behind are measured here and
// published as custom properties on the dialog paper; the layout fit comes from the DOM.

const BOTTOM_INSET_VARIABLE = '--connection-dialog-keyboard-inset';
const TOP_INSET_VARIABLE = '--connection-dialog-viewport-top-inset';
const FOOTER_HEIGHT_VARIABLE = '--connection-dialog-footer-height';
const NARROW_MEDIA_QUERY = '(max-width: 599px)';
const COARSE_POINTER_MEDIA_QUERY = '(pointer: coarse)';

// The content element carries the scoped chain rules; the container carries the band the
// card is placed in. Both follow from the one decision below.
const COMPACT_CLASS = 'connection-dialog-content--compact';
const CONTAINER_KEYBOARD_CLASS = 'connection-dialog-container--keyboard';

const MIN_USABLE_PANEL_HEIGHT = 120;
const REVEAL_MARGIN = 8;
const ZOOM_SCALE_THRESHOLD = 1.01;

const activeAttachments = new Map();
let nextAttachment = 1;

export function attach(contentElement) {
    if (!contentElement) {
        return null;
    }

    const paper = contentElement.closest('.mud-dialog') ?? contentElement;
    // Custom properties inherit downwards, so the strips have to be published on the
    // container the band is spent on, not on the card inside it.
    const container = paper.closest('.mud-dialog-container') ?? paper;
    const viewport = window.visualViewport;
    const narrow = window.matchMedia(NARROW_MEDIA_QUERY);
    const coarsePointer = window.matchMedia(COARSE_POINTER_MEDIA_QUERY);
    let frameHandle = 0;

    const keyboardCanAppear = () => narrow.matches || coarsePointer.matches;

    // The visual viewport is a window onto the layout viewport: the strip above it is
    // the pan iOS applies to reveal a focused field, the strip below it is the keyboard.
    const measureHiddenStrips = () => {
        if (!viewport || viewport.scale > ZOOM_SCALE_THRESHOLD) {
            return { top: 0, bottom: 0 };
        }

        return {
            top: Math.max(0, Math.round(viewport.offsetTop)),
            bottom: Math.max(0, Math.round(
                window.innerHeight - (viewport.height + viewport.offsetTop))),
        };
    };

    const clearInsets = () => {
        container.style.removeProperty(TOP_INSET_VARIABLE);
        container.style.removeProperty(BOTTOM_INSET_VARIABLE);
    };

    // What the panel would get, from the card's own bounded box and the layout heights of
    // the wrapper padding and the title, header, step selector, tab bar and footer around
    // it. client/offset sizes are layout values, so MudBlazor's opening transform cannot
    // scale them, and none of them change because the fallback applied.
    const panelHeight = () => {
        const paperStyle = getComputedStyle(paper);
        const wrapper = paper.querySelector('.mud-dialog-content');
        const wrapperStyle = wrapper ? getComputedStyle(wrapper) : null;
        const wrapperPadding = wrapperStyle
            ? (parseFloat(wrapperStyle.paddingTop) || 0) + (parseFloat(wrapperStyle.paddingBottom) || 0)
            : 0;
        const chrome = [
            paper.querySelector('.mud-dialog-title'),
            contentElement.querySelector('.connection-dialog-header'),
            contentElement.querySelector('.step-selector'),
            contentElement.querySelector('.mud-tabs-tabbar'),
            contentElement.querySelector('.connection-dialog-notices'),
            contentElement.querySelector('.connection-actions-footer'),
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

    // The card scrolls the form in the fallback, the panel does otherwise: chosen by mode,
    // not by whichever candidate happens to report overflow while the other is resizing.
    const scrollOwner = () => {
        if (contentElement.classList.contains(COMPACT_CLASS)) {
            return contentElement.scrollHeight > contentElement.clientHeight + 1 ? contentElement : null;
        }

        const panel = contentElement.querySelector('.mud-tab-panel-active');
        return panel && panel.scrollHeight > panel.clientHeight + 1 ? panel : null;
    };

    // The footer is stuck over the bottom of the card in the fallback, so the band a
    // control may land in stops at the footer rather than at the scroll owner's box.
    const revealFocusedField = () => {
        const active = document.activeElement;
        if (!active || !contentElement.contains(active) || !isTextControl(active)) {
            return;
        }

        const scroller = scrollOwner();
        if (!scroller) {
            return;
        }

        // The dialog scales in while it opens, which scales every measured rect with it;
        // the ratio of the paper's painted width to its layout width takes that back out.
        const paperBox = paper.getBoundingClientRect();
        const scale = paper.offsetWidth > 0 ? paperBox.width / paper.offsetWidth : 1;
        const scrollerBox = scroller.getBoundingClientRect();
        const fieldBox = active.getBoundingClientRect();
        const footer = contentElement.querySelector('.connection-actions-footer');
        const footerOverlays = footer !== null && scroller !== footer && scroller.contains(footer);

        const bandTop = scrollerBox.top;
        const bandBottom = footerOverlays
            ? Math.min(footer.getBoundingClientRect().top, scrollerBox.bottom)
            : scrollerBox.bottom;

        // The margins give way when the band is only just tall enough for the control.
        const margin = Math.min(REVEAL_MARGIN, Math.max(0, bandBottom - bandTop - fieldBox.height) / 2);
        const visibleTop = bandTop + margin;
        const visibleBottom = bandBottom - margin;

        if (fieldBox.top >= visibleTop && fieldBox.bottom <= visibleBottom) {
            return;
        }

        // A control taller than the band cannot be shown whole; flush to the band's top
        // shows the most of it, where aligning its bottom pushes its top out of the card.
        const tallerThanBand = fieldBox.height > visibleBottom - visibleTop;
        const delta = tallerThanBand || fieldBox.top < visibleTop
            ? fieldBox.top - visibleTop
            : fieldBox.bottom - visibleBottom;
        scroller.scrollTop = Math.max(0, scroller.scrollTop + (delta / scale));
    };

    const setCompact = (compact) => {
        contentElement.classList.toggle(COMPACT_CLASS, compact);
    };

    const apply = () => {
        const eligible = keyboardCanAppear();
        const strips = eligible ? measureHiddenStrips() : { top: 0, bottom: 0 };
        const hidden = eligible && (strips.top > 0 || strips.bottom > 0);

        if (eligible) {
            container.style.setProperty(TOP_INSET_VARIABLE, `${strips.top}px`);
            container.style.setProperty(BOTTOM_INSET_VARIABLE, `${strips.bottom}px`);
        } else {
            clearInsets();
        }

        // The container, not the card, carries the band: a card taken out of flow is painted
        // under the provider's backdrop and swallows the taps that reach it.
        container.classList.toggle(CONTAINER_KEYBOARD_CLASS, hidden);

        // A stuck footer is where a scroll container's overflow stops counting, so the card
        // reserves its height as trailing space and the last control can still clear it.
        const footer = contentElement.querySelector('.connection-actions-footer');
        container.style.setProperty(FOOTER_HEIGHT_VARIABLE, `${footer ? footer.offsetHeight : 0}px`);

        const compact = panelHeight() < MIN_USABLE_PANEL_HEIGHT;
        setCompact(compact);

        if (hidden || compact) {
            revealFocusedField();
        }
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

    // A footer alert (a name collision, a failed connection) or a growing panel changes what
    // the panel has left with no viewport or focus event behind it. apply() only toggles
    // classes and custom properties, so a settled dialog cannot feed this back.
    const observer = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(schedule);
    observer?.observe(paper);
    observer?.observe(contentElement);
    const footer = contentElement.querySelector('.connection-actions-footer');
    if (footer) {
        observer?.observe(footer);
    }

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
        viewport?.removeEventListener('resize', schedule);
        viewport?.removeEventListener('scroll', schedule);
        window.removeEventListener('resize', schedule);
        window.removeEventListener('orientationchange', schedule);
        narrow.removeEventListener('change', schedule);
        coarsePointer.removeEventListener('change', schedule);
        contentElement.removeEventListener('focusin', schedule);
        clearInsets();
        container.classList.remove(CONTAINER_KEYBOARD_CLASS);
        setCompact(false);
    };

    viewport?.addEventListener('resize', schedule);
    viewport?.addEventListener('scroll', schedule);
    window.addEventListener('resize', schedule);
    window.addEventListener('orientationchange', schedule);
    narrow.addEventListener('change', schedule);
    coarsePointer.addEventListener('change', schedule);
    contentElement.addEventListener('focusin', schedule);

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
