const HANDLED_KEYS = new Set(['ArrowDown', 'ArrowUp', 'Home', 'End', 'Enter', ' ']);
const GESTURE_EVENTS = ['wheel', 'pointerdown', 'touchstart', 'touchmove'];
const EDITABLE_SELECTOR = 'input, textarea, select, [contenteditable="true"]';
const INTERACTIVE_SELECTOR = 'button, a[href], [role="button"]';

const REVEAL_MAX_FRAMES = 60;
const REVEAL_TIME_BUDGET_MS = 900;

const attachments = new Map();
const reveals = new WeakMap();
const gestureBlocked = new WeakMap();
let nextAttachment = 1;

const ownsKeyboard = (element) => element.isConnected && document.activeElement === element;

const isNavigationTarget = (target, list) => {
    if (!(target instanceof Element)) {
        return false;
    }

    const onList = target === list || target.closest('tr.payload-row') !== null;
    return onList && target.closest(EDITABLE_SELECTOR) === null && target.closest(INTERACTIVE_SELECTOR) === null;
};

const preventHandledKeyDefault = (event) => {
    // An IME owns the keys it is composing with.
    if (event.defaultPrevented || event.isComposing || event.keyCode === 229) {
        return;
    }

    if (event.ctrlKey || event.altKey || event.metaKey) {
        return;
    }

    if (!HANDLED_KEYS.has(event.key) || !isNavigationTarget(event.target, event.currentTarget)) {
        return;
    }

    gestureBlocked.delete(event.currentTarget);
    event.preventDefault();
};

// Any pointer, wheel or touch scroll is the user taking over, so a reveal has to yield.
const onGesture = (event) => {
    cancelReveal(event.currentTarget);
    gestureBlocked.set(event.currentTarget, true);
};

const rowIndexOf = (row) =>
    Number(row.querySelector('[data-message-index]')?.dataset.messageIndex ?? -1);

const findRenderedRow = (element, index) =>
    Array.from(element.querySelectorAll('tbody tr.payload-row'))
        .find((row) => rowIndexOf(row) === index) ?? null;

const scrollToward = (element, index) => {
    const rows = element.querySelectorAll('tbody tr.payload-row');
    const scroller = element.querySelector('.mud-table-container');
    if (rows.length === 0 || !scroller) {
        return false;
    }

    const first = rowIndexOf(rows[0]);
    const last = rowIndexOf(rows[rows.length - 1]);
    if (first < 0 || last < 0) {
        return false;
    }

    // One row-height estimate per step; the next frame re-measures after the virtualizer re-renders.
    const rowHeight = rows[0].getBoundingClientRect().height || 1;
    const before = scroller.scrollTop;
    scroller.scrollTop = before + (index < first ? index - first : index - last) * rowHeight;
    return scroller.scrollTop !== before;
};

const cancelReveal = (element) => {
    const state = reveals.get(element);
    if (!state) {
        return;
    }

    if (state.frame) {
        cancelAnimationFrame(state.frame);
    }
    reveals.delete(element);
};

const revealStep = (element, index, state) => {
    state.frame = 0;

    const abandoned = state.frames >= REVEAL_MAX_FRAMES
        || Date.now() - state.startedAt > REVEAL_TIME_BUDGET_MS
        || !element.isConnected
        || document.activeElement !== element;

    if (abandoned) {
        reveals.delete(element);
        return;
    }

    state.frames += 1;

    const row = findRenderedRow(element, index);
    if (row) {
        row.scrollIntoView({ block: 'nearest' });
        reveals.delete(element);
        return;
    }

    if (!scrollToward(element, index)) {
        reveals.delete(element);
        return;
    }

    state.frame = requestAnimationFrame(() => revealStep(element, index, state));
};

export function reveal(element, index) {
    if (!element) {
        return false;
    }

    cancelReveal(element);

    if (index < 0 || !ownsKeyboard(element) || gestureBlocked.get(element)) {
        return false;
    }

    const row = findRenderedRow(element, index);
    if (row) {
        row.scrollIntoView({ block: 'nearest' });
        return true;
    }

    if (!scrollToward(element, index)) {
        return false;
    }

    const state = { frames: 0, startedAt: Date.now(), frame: 0 };
    reveals.set(element, state);
    state.frame = requestAnimationFrame(() => revealStep(element, index, state));
    return false;
}

export function attach(element) {
    if (!element) {
        return null;
    }

    gestureBlocked.delete(element);
    element.addEventListener('keydown', preventHandledKeyDefault);
    for (const type of GESTURE_EVENTS) {
        element.addEventListener(type, onGesture, { passive: true });
    }

    const attachment = nextAttachment++;
    attachments.set(attachment, element);
    return attachment;
}

export function detach(attachment) {
    const element = attachments.get(attachment);
    if (!element) {
        return;
    }

    cancelReveal(element);
    gestureBlocked.delete(element);
    element.removeEventListener('keydown', preventHandledKeyDefault);
    for (const type of GESTURE_EVENTS) {
        element.removeEventListener(type, onGesture);
    }

    attachments.delete(attachment);
}
