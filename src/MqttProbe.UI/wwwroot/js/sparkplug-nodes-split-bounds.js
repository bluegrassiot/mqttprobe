// MudSplitPanel 9.11 only has one minimum size for both panes. The Nodes split also needs a
// list cap and a detail floor, so this wraps the bounds and ARIA paths around its existing
// interaction while leaving MudBlazor's own drag listeners in place.

const ATTACHMENT_ATTRIBUTE = 'spbSplitBounds';
const activeAttachments = new Map();
const activeRebases = new Set();
let nextAttachment = 1;
let interactionHandlerInstalled = false;

export function install() {
    installInteractionHandler();
}

export function attach(wrapper) {
    installInteractionHandler();

    if (!wrapper) {
        return null;
    }

    const panel = wrapper.querySelector('.mud-split-panel.app-split-panel');
    if (!panel) {
        return null;
    }

    const existing = Number(panel.dataset[ATTACHMENT_ATTRIBUTE] || 0);
    if (existing && activeAttachments.has(existing)) {
        return existing;
    }

    const runtime = window.splitPanels?.[panel.id];
    if (!runtime?.container || !runtime.firstPanel || !runtime.secondPanel || !runtime.divider
        || typeof runtime._setPanelSizes !== 'function'
        || typeof runtime._getContainerSize !== 'function'
        || typeof runtime.update !== 'function') {
        return null;
    }

    const style = getComputedStyle(wrapper);
    const maxShare = readNumber(style, '--spb-list-max-share', 55);
    const detailMin = readNumber(style, '--spb-detail-min', 500);

    const containerSize = () => {
        const box = runtime.container.getBoundingClientRect();
        const size = runtime.horizontal ? box.height : box.width;
        return size > 0 ? size : 0;
    };

    const bounds = () => {
        const size = containerSize();
        const min = runtime.minPanelSize;
        const gap = Number(runtime.panelGap) || 0;
        const max = size > 0
            ? Math.max(min, Math.min(size * maxShare / 100, size - gap - detailMin))
            : 0;
        return { size, min, max };
    };

    const firstSize = () => runtime.horizontal
        ? runtime.firstPanel.offsetHeight
        : runtime.firstPanel.offsetWidth;

    const declaredSize = () => {
        const value = runtime.horizontal
            ? runtime.firstPanel.style.height
            : runtime.firstPanel.style.width;
        const parsed = Number.parseFloat(value);
        return value?.endsWith('px') && Number.isFinite(parsed) ? parsed : firstSize();
    };

    const syncAria = () => {
        const { size, min, max } = bounds();
        if (size <= 0) {
            return;
        }

        runtime.divider.ariaValueMin = (min / size * 100).toFixed(2);
        runtime.divider.ariaValueMax = (max / size * 100).toFixed(2);
        runtime.divider.ariaValueNow = (firstSize() / size * 100).toFixed(2);
    };

    // The framework's update runs after every parent render and rewrites aria-valuemax, so the
    // bound is re-applied there rather than only when the divider is used.
    const applyBounds = () => {
        const { size, min, max } = bounds();
        if (size <= 0) {
            return;
        }

        const width = firstSize();
        const declared = declaredSize();
        if (declared > max + 0.5 || width > max + 0.5) {
            runtime._setPanelSizes(max, runtime._getContainerSize());
        } else if (declared < min - 0.5 || width < min - 0.5) {
            runtime._setPanelSizes(min, runtime._getContainerSize());
        }

        syncAria();
    };

    const originalSetPanelSizes = runtime._setPanelSizes;
    const originalUpdate = runtime.update;
    const originalDestroy = runtime.destroy;
    let attachment = 0;
    let observer = null;
    let tornDown = false;
    let teardown = () => {};

    runtime._setPanelSizes = function (first, container) {
        const { size, min, max } = bounds();
        if (size <= 0) {
            originalSetPanelSizes.call(this, first, this._getContainerSize());
            return;
        }

        const bounded = Math.min(Math.max(first, min), max);
        originalSetPanelSizes.call(this, bounded, this._getContainerSize());
    };

    // This runs in the capture phase before MudBlazor's document mousemove listener. Rebasing
    // there stops pointer travel beyond the visual stop accumulating, so a reverse move takes
    // effect immediately. It does not replace or remove the framework listener.
    const rebase = (event) => {
        if (!runtime.isDragging) {
            return;
        }

        const point = event.touches?.[0] ?? event;
        const position = runtime.horizontal ? point.clientY : point.clientX;
        const desired = runtime.startFirstSize + (position - runtime.startPos);
        const { size, min, max } = bounds();
        if (size <= 0 || (desired >= min && desired <= max)) {
            return;
        }

        const bounded = Math.min(Math.max(desired, min), max);
        runtime._setPanelSizes(bounded, runtime._getContainerSize());
        runtime.dividerHasMoved = true;
        runtime.startFirstSize = bounded;
        runtime.startPos = position;
    };

    activeRebases.add(rebase);

    runtime.update = function (...args) {
        originalUpdate.apply(this, args);
        applyBounds();
    };

    teardown = () => {
        if (tornDown) {
            return;
        }

        tornDown = true;
        observer?.disconnect();
        activeRebases.delete(rebase);
        if (window.splitPanels?.[panel.id] === runtime) {
            runtime.update = originalUpdate;
            runtime._setPanelSizes = originalSetPanelSizes;
            runtime.destroy = originalDestroy;
        }

        delete panel.dataset[ATTACHMENT_ATTRIBUTE];
        delete runtime.__spbBounds;
        activeAttachments.delete(attachment);
    };

    runtime.destroy = function () {
        teardown();
        originalDestroy.call(this);
    };

    attachment = nextAttachment++;
    activeAttachments.set(attachment, teardown);
    panel.dataset[ATTACHMENT_ATTRIBUTE] = String(attachment);
    runtime.__spbBounds = attachment;

    if (typeof ResizeObserver !== 'undefined') {
        observer = new ResizeObserver(() => applyBounds());
        observer.observe(runtime.container);
        observer.observe(wrapper);
    }

    applyBounds();
    return attachment;
}

export function detach(attachment) {
    activeAttachments.get(attachment)?.();
}

function readNumber(style, property, fallback) {
    const value = Number.parseFloat(style.getPropertyValue(property));
    return Number.isFinite(value) ? value : fallback;
}

// A re-created split (breakpoint remount) gets its bounds before MudSplitPanel handles the
// first drag or key press, even when the parent has not rendered since the remount.
function installInteractionHandler() {
    if (interactionHandlerInstalled) {
        return;
    }

    interactionHandlerInstalled = true;
    const attachOnInteraction = (event) => {
        const target = event.target instanceof Element ? event.target : null;
        const wrapper = target?.closest('.spb-split-wrap');
        if (!wrapper) {
            return;
        }

        const panel = wrapper.querySelector('.mud-split-panel.app-split-panel');
        if (panel) {
            attach(wrapper);
        }
    };

    for (const type of ['mousedown', 'touchstart', 'keydown', 'focusin', 'mouseover']) {
        document.addEventListener(type, attachOnInteraction, true);
    }

    const rebaseDrag = (event) => {
        for (const handler of activeRebases) {
            handler(event);
        }
    };
    document.addEventListener('mousemove', rebaseDrag, true);
    document.addEventListener('touchmove', rebaseDrag, true);
}
