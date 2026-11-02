// iOS keeps the layout viewport at full height while the keyboard is open, so the strips its visual viewport leaves are measured and published as custom properties.

const BOTTOM_INSET_VARIABLE = '--connection-dialog-keyboard-inset';
const TOP_INSET_VARIABLE = '--connection-dialog-viewport-top-inset';
const NARROW_MEDIA_QUERY = '(max-width: 599px)';
const COARSE_POINTER_MEDIA_QUERY = '(pointer: coarse)';

const COMPACT_CLASS = 'connection-dialog-content--compact';
const CONTAINER_KEYBOARD_CLASS = 'connection-dialog-container--keyboard';

const MIN_USABLE_PANEL_HEIGHT = 120;
const REVEAL_MARGIN = 8;
const ZOOM_SCALE_THRESHOLD = 1.01;

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
        if (contentElement.classList.contains(COMPACT_CLASS)) {
            const form = contentElement.querySelector('.mud-form');
            return form && form.scrollHeight > form.clientHeight + 1 ? form : null;
        }

        const panel = contentElement.querySelector('.mud-tab-panel-active');
        return panel && panel.scrollHeight > panel.clientHeight + 1 ? panel : null;
    };

    // The scroll owner's box ends the band a control may land in.
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

        const bandTop = scrollerBox.top;
        const bandBottom = scrollerBox.bottom;

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
