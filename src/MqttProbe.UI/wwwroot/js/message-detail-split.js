// MudSplitPanel only knows a fixed initial size, but the metadata block is a
// different height for every message. Size the upper pane to its content when
// the detail opens, clamped so the payload keeps a real share of the height.
// A drag afterwards is left alone.

const MAX_META_SHARE = 0.4;
const MIN_PAYLOAD_HEIGHT = 200;
const resetListeners = new WeakSet();

function attachResetListener(panel, metaPane) {
    const divider = panel.querySelector('.divider');
    if (!divider || resetListeners.has(divider)) {
        return;
    }

    resetListeners.add(divider);
    divider.addEventListener('dblclick', () => {
        resetListeners.delete(divider);
        requestAnimationFrame(() => fitToContent(metaPane));
    }, { once: true });
}

export function fitToContent(metaPane) {
    const panel = metaPane?.closest('.mud-split-panel');
    const table = metaPane?.querySelector('.pb-meta-table');
    if (!panel || !table) {
        return false;
    }

    const runtime = window.splitPanels?.[panel.id];
    if (!runtime?.container || typeof window.mudSplitPanel_setDividerPosition !== 'function') {
        return false;
    }

    // A split inside a hidden tab measures zero; the caller retries on the next render.
    const total = runtime.container.clientHeight;
    if (total <= 0) {
        return false;
    }

    const style = getComputedStyle(metaPane);
    const padding = (parseFloat(style.paddingTop) || 0) + (parseFloat(style.paddingBottom) || 0);
    const content = table.getBoundingClientRect().height + padding;
    const gap = Number(runtime.panelGap) || 0;
    const min = Number(runtime.minPanelSize) || 0;
    const max = Math.max(min, Math.min(total * MAX_META_SHARE, total - gap - MIN_PAYLOAD_HEIGHT));

    window.mudSplitPanel_setDividerPosition(
        panel.id,
        Math.round(Math.min(Math.max(content, min), max)));
    attachResetListener(panel, metaPane);
    return true;
}
