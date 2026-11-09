const HANDLED_KEYS = new Set(['ArrowRight', 'ArrowLeft']);

const attachments = new Map();
let nextAttachment = 1;

const isExpanded = (toggle) => toggle.getAttribute('aria-expanded') === 'true';

// Only the arrow that opens or closes this node is consumed; the other one keeps its browser
// default, so a key the expander ignores still scrolls the panel.
const togglesInDirection = (toggle, key) =>
    key === 'ArrowRight' ? !isExpanded(toggle) : isExpanded(toggle);

const preventToggleKeyDefault = (event) => {
    // An IME owns the keys it is composing with.
    if (event.defaultPrevented || event.isComposing || event.keyCode === 229) {
        return;
    }

    if (event.ctrlKey || event.altKey || event.metaKey || event.shiftKey) {
        return;
    }

    if (!HANDLED_KEYS.has(event.key)) {
        return;
    }

    const target = event.target;
    const toggle = target instanceof Element ? target.closest('.json-toggle') : null;
    if (!toggle || !event.currentTarget.contains(toggle) || !togglesInDirection(toggle, event.key)) {
        return;
    }

    event.preventDefault();
};

export function attach(element) {
    if (!element) {
        return null;
    }

    element.addEventListener('keydown', preventToggleKeyDefault);

    const attachment = nextAttachment++;
    attachments.set(attachment, element);
    return attachment;
}

export function detach(attachment) {
    const element = attachments.get(attachment);
    if (!element) {
        return;
    }

    element.removeEventListener('keydown', preventToggleKeyDefault);
    attachments.delete(attachment);
}
