// Focus follows the row key rather than a DOM position: a live update or the virtualizer can
// replace the element while the row keeps its identity.

const ITEM_HEIGHT_FALLBACK = 36;
const FOCUS_WAIT_FRAMES = 20;

const byToken = new Map();
const byContainer = new WeakMap();

export function attach(container, token, dotNetRef) {
    if (!container || !token) {
        return false;
    }

    const existing = byToken.get(token);
    if (existing && existing.container === container) {
        return true;
    }

    if (existing) {
        detach(token);
    }

    const state = {
        container,
        dotNetRef: dotNetRef ?? null,
        request: 0,
        frame: 0,
        tabFrame: 0,
        suppress: false,
        departed: false,
        detached: false,
        lastFocused: null,
        lastTabStop: null,
        committedKey: null,
        reportedKey: null,
        notified: null
    };

    const onFocusIn = (event) => {
        state.request++;
        state.suppress = false;
        state.departed = false;
        state.notified = null;
        state.reportedKey = null;

        const row = event.target instanceof Element ? event.target.closest('[data-topic-key]') : null;
        if (row) {
            state.lastFocused = row;
            state.committedKey = row.getAttribute('data-topic-key');
            rememberTabStop(state, row);
        }
    };
    // A null related target is a body fallback, which the ownership check judges; only a real
    // move to another control is an explicit departure.
    const onFocusOut = (event) => {
        const next = event.relatedTarget;
        if (next) {
            state.request++;
            if (!(next instanceof Node && state.container.contains(next))) {
                state.departed = true;
            }
        }

        // Before focus can come back with Tab, the tree needs a reachable stop again.
        reconcileTabStop(state, true);
    };
    // A pointer or wheel gesture means the user is steering the tree, so automatic focus
    // recovery stands down until a row is focused or a key is pressed again.
    const onGesture = () => {
        state.request++;
        state.suppress = true;
    };

    // A child render can unmount the focused row without a parent render to ask for it back, so
    // the loss itself is reported once and the component decides the replacement.
    const observer = typeof MutationObserver === 'undefined' ? null : new MutationObserver(() => {
        scheduleTabStopCheck(state);

        if (state.suppress || state.departed || state.frame !== 0) {
            return;
        }

        if (document.activeElement !== document.body || !state.lastFocused || state.lastFocused.isConnected) {
            return;
        }

        if (state.notified === state.lastFocused) {
            return;
        }

        state.notified = state.lastFocused;
        try {
            const pending = state.dotNetRef?.invokeMethodAsync('OnFocusedRowUnmountedAsync');
            if (pending && typeof pending.catch === 'function') {
                pending.catch(() => {});
            }
        } catch {
            // The circuit is gone; nothing is left to focus.
        }
    });

    // The virtualizer mounts and unmounts rows on its own, so the tab stop is checked against
    // the rows that are really there, not only when a key is pressed.
    observer?.observe(container, { childList: true, subtree: true, attributes: true, attributeFilter: ['tabindex'] });

    // The scrollbar belongs to the scroll owner above the tree, and its drag does not travel
    // through the tree element, so the gesture pause listens on both.
    const scrollOwner = closestScroller(container);
    const gestureTargets = scrollOwner && scrollOwner !== container ? [container, scrollOwner] : [container];
    for (const target of gestureTargets) {
        target.addEventListener('wheel', onGesture, { passive: true });
        target.addEventListener('pointerdown', onGesture);
        target.addEventListener('touchstart', onGesture, { passive: true });
        target.addEventListener('touchmove', onGesture, { passive: true });
    }

    container.addEventListener('focusin', onFocusIn);
    container.addEventListener('focusout', onFocusOut);

    byToken.set(token, { state, observer, gestureTargets, onFocusIn, onFocusOut, onGesture });
    byContainer.set(container, state);
    return true;
}

export function detach(token) {
    const attachment = byToken.get(token);
    if (!attachment) {
        return false;
    }

    byToken.delete(token);
    attachment.state.detached = true;
    cancelPending(attachment.state);
    attachment.observer?.disconnect();
    byContainer.delete(attachment.state.container);

    const container = attachment.state.container;
    container.removeEventListener('focusin', attachment.onFocusIn);
    container.removeEventListener('focusout', attachment.onFocusOut);
    for (const target of attachment.gestureTargets) {
        target.removeEventListener('wheel', attachment.onGesture);
        target.removeEventListener('pointerdown', attachment.onGesture);
        target.removeEventListener('touchstart', attachment.onGesture);
        target.removeEventListener('touchmove', attachment.onGesture);
    }

    return true;
}

// A keyboard move is explicit intent, so it may clear the gesture pause and continue while the
// browser holds focus on the body because our own scroll removed the origin row.
export function moveFocus(container, key, index, itemHeight) {
    return focus(container, key, index, itemHeight, true);
}

// Recovery after a removal: it never clears the gesture pause and only trusts body focus when
// the row we owned is gone.
export function restoreFocus(container, key, index, itemHeight) {
    return focus(container, key, index, itemHeight, false);
}

// Runs after a render. A row that is still rendered means the user moved focus on purpose.
export function retainFocus(container, key, index, itemHeight) {
    const state = byContainer.get(container);
    if (!state || state.suppress || !key) {
        return false;
    }

    // A move still waiting for its row to render wins: keeping the row it is leaving would
    // cancel the move and pull the view back.
    if (state.frame) {
        return false;
    }

    if (document.activeElement !== document.body || findRow(container, key) || !ownsFocus(state)) {
        return false;
    }

    return revealRow(state, key, index, itemHeight);
}

function focus(container, key, index, itemHeight, explicit) {
    const state = byContainer.get(container);
    if (!state || !key) {
        return false;
    }

    state.committedKey = key;
    if (explicit) {
        state.suppress = false;
    } else if (state.suppress) {
        return false;
    }

    cancelPending(state);
    if (!ownsFocus(state)) {
        return false;
    }

    const row = findRow(container, key);
    if (row) {
        row.focus();
        return document.activeElement === row;
    }

    return revealRow(state, key, index, itemHeight);
}

// Focus belongs to the tree while it holds an element inside it. The page body counts only when
// the row the tree last owned is disconnected, which is what our own scroll or a render does.
function ownsFocus(state) {
    const active = document.activeElement;
    if (active instanceof Node && state.container.contains(active)) {
        return true;
    }

    if (state.departed || active !== document.body) {
        return false;
    }

    return state.lastFocused !== null && !state.lastFocused.isConnected;
}

function findRow(container, key) {
    for (const row of container.querySelectorAll('[data-topic-key]')) {
        if (row.getAttribute('data-topic-key') === key) {
            return row;
        }
    }
    return null;
}

// The component marks the row it committed as the tab stop, but the virtualizer renders only the
// rows in view. A committed row that is not mounted would leave the tree with no tab stop at all,
// so the marked row is kept when it is still there and a mounted row takes the stop otherwise.
// This only changes what Tab can reach; it never focuses or scrolls the tree.
function reconcileTabStop(state, report) {
    if (!state || state.detached || !state.container.isConnected) {
        return;
    }

    const rows = mountedRows(state.container);
    if (rows.length === 0) {
        return;
    }

    const stops = rows.filter((row) => row.getAttribute('tabindex') === '0');
    if (stops.length === 1) {
        rememberTabStop(state, stops[0]);
        return;
    }

    // The row the component committed wins when it is rendered; otherwise the row that last held
    // the stop, then the last or first rendered row, so the choice is the same for the same tree.
    const committed = state.committedKey ? findRow(state.container, state.committedKey) : null;
    const next = committed ?? rememberedTabStop(state) ?? stops[stops.length - 1] ?? rows[0];

    for (const row of stops) {
        if (row !== next) {
            row.setAttribute('tabindex', '-1');
        }
    }
    if (next.getAttribute('tabindex') !== '0') {
        next.setAttribute('tabindex', '0');
    }

    rememberTabStop(state, next);
    if (!report || (!state.departed && state.frame !== 0)) {
        return;
    }

    const key = next.getAttribute('data-topic-key');
    if (!key || state.reportedKey === key) {
        return;
    }

    state.reportedKey = key;
    state.committedKey = key;
    try {
        const pending = state.dotNetRef?.invokeMethodAsync('OnTabStopFallbackAsync', key);
        if (pending && typeof pending.catch === 'function') {
            pending.catch(() => {});
        }
    } catch {
        // The circuit is gone; the DOM keeps the stop this page gave it.
    }
}

function scheduleTabStopCheck(state) {
    if (state.detached || state.tabFrame) {
        return;
    }

    state.tabFrame = window.requestAnimationFrame(() => {
        state.tabFrame = 0;
        reconcileTabStop(state, true);
    });
}

function mountedRows(container) {
    return Array.from(container.querySelectorAll('[data-topic-key]'));
}

function rememberTabStop(state, row) {
    state.lastTabStop = { element: row, key: row.getAttribute('data-topic-key') };
}

function rememberedTabStop(state) {
    const remembered = state.lastTabStop;
    if (!remembered || !remembered.element.isConnected) {
        return null;
    }

    return remembered.element.getAttribute('data-topic-key') === remembered.key ? remembered.element : null;
}

// The virtualizer renders only the rows near the viewport, so a row just past the window is
// scrolled into range and focused once it has rendered. An attempt stops as soon as another
// request, a gesture, or focus itself moves on.
function revealRow(state, key, index, itemHeight) {
    if (index < 0) {
        return false;
    }

    cancelPending(state);
    const request = state.request;
    const scroller = closestScroller(state.container);
    const rowHeight = itemHeight > 0 ? itemHeight : ITEM_HEIGHT_FALLBACK;
    if (scroller) {
        scroller.scrollTop = Math.max(0, index * rowHeight - (scroller.clientHeight - rowHeight) / 2);
    }

    let frames = 0;
    const focusWhenRendered = () => {
        state.frame = 0;
        if (request !== state.request || state.suppress || !state.container.isConnected) {
            return;
        }

        if (!ownsFocus(state)) {
            return;
        }

        const row = findRow(state.container, key);
        if (row) {
            row.focus();
            return;
        }

        if (++frames < FOCUS_WAIT_FRAMES) {
            state.frame = window.requestAnimationFrame(focusWhenRendered);
        }
    };

    state.frame = window.requestAnimationFrame(focusWhenRendered);
    return true;
}

function cancelPending(state) {
    if (state.frame) {
        window.cancelAnimationFrame(state.frame);
        state.frame = 0;
    }
    if (state.tabFrame) {
        window.cancelAnimationFrame(state.tabFrame);
        state.tabFrame = 0;
    }
    state.request++;
}

function closestScroller(element) {
    for (let node = element.parentElement; node && node !== document.body; node = node.parentElement) {
        if (window.getComputedStyle(node).overflowY !== 'visible') {
            return node;
        }
    }
    return null;
}
