// iOS keeps the layout viewport at full height while the keyboard is open, so the strips its visual viewport leaves are measured and published as custom properties.

const BOTTOM_INSET_VARIABLE = '--connection-dialog-keyboard-inset';
const TOP_INSET_VARIABLE = '--connection-dialog-viewport-top-inset';
const FOOTER_HEIGHT_VARIABLE = '--connection-dialog-footer-height';
const NARROW_MEDIA_QUERY = '(max-width: 599px)';
const COARSE_POINTER_MEDIA_QUERY = '(pointer: coarse)';

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
    // Custom properties inherit downwards, so the strips are published on the container the band is spent on.
    const container = paper.closest('.mud-dialog-container') ?? paper;
    const viewport = window.visualViewport;
    const narrow = window.matchMedia(NARROW_MEDIA_QUERY);
    const coarsePointer = window.matchMedia(COARSE_POINTER_MEDIA_QUERY);
    let frameHandle = 0;

    const keyboardCanAppear = () => narrow.matches || coarsePointer.matches;

    // Above the visual viewport is iOS's pan to reveal the focused field; below it is the keyboard.
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

    // Mode picks the scroll owner, so a transient overflow reading cannot flip it.
    const scrollOwner = () => {
        if (contentElement.classList.contains(COMPACT_CLASS)) {
            return contentElement.scrollHeight > contentElement.clientHeight + 1 ? contentElement : null;
        }

        const panel = contentElement.querySelector('.mud-tab-panel-active');
        return panel && panel.scrollHeight > panel.clientHeight + 1 ? panel : null;
    };

    // The stuck footer ends the band a control may land in, not the scroll owner's box.
    const revealFocusedField = () => {
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

        // The container carries the band, not the card: a card out of flow paints under the backdrop and swallows taps.
        container.classList.toggle(CONTAINER_KEYBOARD_CLASS, hidden);

        // The card reserves the stuck footer's height as trailing space so the last control can clear it.
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

    // An alert or a growing panel resizes the chrome with no viewport event; apply() only toggles classes, so it cannot feed back.
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
