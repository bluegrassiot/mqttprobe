import { readFile } from 'node:fs/promises';
import { runInNewContext } from 'node:vm';

const appScriptUrl = new URL('../../src/MqttProbe.UI/wwwroot/js/app-keyboard-viewport.js', import.meta.url);
const dialogScriptUrl = new URL('../../src/MqttProbe.UI/wwwroot/js/connection-dialog-viewport.js', import.meta.url);

export class FakeEvent {
    constructor(type) {
        this.type = type;
    }
}

class FakeClassList {
    constructor(initial = []) {
        this.names = new Set(initial);
    }

    add(...names) {
        names.forEach((name) => this.names.add(name));
    }

    remove(...names) {
        names.forEach((name) => this.names.delete(name));
    }

    contains(name) {
        return this.names.has(name);
    }

    toggle(name, force) {
        const next = force === undefined ? !this.names.has(name) : Boolean(force);
        if (next) {
            this.names.add(name);
        } else {
            this.names.delete(name);
        }

        return next;
    }

    get value() {
        return [...this.names].join(' ');
    }
}

class FakeStyle {
    constructor() {
        this.values = new Map();
    }

    setProperty(name, value) {
        this.values.set(name, String(value));
    }

    removeProperty(name) {
        this.values.delete(name);
    }

    getPropertyValue(name) {
        return this.values.get(name) ?? '';
    }
}

class FakeHTMLElement {
    constructor(initialClasses = []) {
        this.classList = new FakeClassList(initialClasses);
        this.style = new FakeStyle();
        this.attributes = {};
        this.parentElement = null;
    }

    getAttribute(name) {
        return Object.prototype.hasOwnProperty.call(this.attributes, name) ? this.attributes[name] : null;
    }

    setAttribute(name, value) {
        this.attributes[name] = String(value);
    }

    hasAttribute(name) {
        return Object.prototype.hasOwnProperty.call(this.attributes, name);
    }

    removeAttribute(name) {
        delete this.attributes[name];
    }

    matches() {
        return false;
    }
}

function createElement(classes, owner) {
    const element = new FakeHTMLElement(classes);
    element.closest = () => null;
    element.querySelector = () => null;
    element.querySelectorAll = () => [];
    element.contains = () => false;
    element.addEventListener = () => {};
    element.removeEventListener = () => {};
    element.getBoundingClientRect = () => ({ top: 0, bottom: owner?.layoutHeight ?? 0, width: 600, height: owner?.layoutHeight ?? 0 });
    element.scrollIntoView = () => {};
    return element;
}

function createEventTarget() {
    const listeners = new Map();
    return {
        listeners,
        addEventListener(type, handler) {
            if (!listeners.has(type)) {
                listeners.set(type, []);
            }

            listeners.get(type).push(handler);
        },
        removeEventListener(type, handler) {
            listeners.set(type, (listeners.get(type) ?? []).filter((item) => item !== handler));
        },
        dispatchEvent(event) {
            (listeners.get(event.type) ?? []).forEach((handler) => handler(event));
            return true;
        },
    };
}

export async function createShell(options = {}) {
    const state = {
        width: options.width ?? 951,
        height: options.height ?? 669,
        rootClient: options.rootClient ?? options.height ?? 669,
        rootScroll: options.rootScroll ?? options.height ?? 669,
        viewportHeight: options.viewportHeight ?? null,
        viewportTop: options.viewportTop ?? 0,
        scale: options.scale ?? 1,
        coarse: options.coarse ?? true,
        activeElement: null,
        now: 0,
    };
    state.viewportHeight ??= state.height;

    const root = createElement([], state);
    Object.defineProperty(root, 'clientHeight', { configurable: true, get: () => state.rootClient });
    Object.defineProperty(root, 'scrollHeight', { configurable: true, get: () => state.rootScroll });
    const documentTarget = createEventTarget();
    const windowTarget = createEventTarget();
    const viewportTarget = createEventTarget();
    const frames = [];
    const timers = [];
    const scrollCalls = [];
    let timerId = 1;

    const dialogContainer = createElement(['mud-dialog-container'], state);
    const paper = createElement(['mud-dialog'], state);
    paper.closest = (selector) => (selector === '.mud-dialog-container' ? dialogContainer : null);
    const dialogContent = createElement(['connection-dialog-content'], state);
    dialogContent.closest = (selector) => (selector === '.mud-dialog' ? paper : null);
    const form = {
        id: 'form',
        scrollHeight: 0,
        clientHeight: 120,
        scrollTop: 0,
        getBoundingClientRect: () => ({ top: 0, bottom: form.clientHeight, left: 0, right: 600 }),
    };
    dialogContent.form = form;
    const topic = new FakeHTMLElement(['mqtt-topic-input']);
    topic.matches = (selector) => selector.includes('input');
    topic.closest = (selector) => (selector === '.connection-dialog-content'
        ? dialogContent
        : selector === '.mud-dialog-container'
            ? dialogContainer
            : null);
    topic.getBoundingClientRect = () => ({ top: 40, bottom: 60, left: 0, right: 100, width: 100, height: 20 });
    topic.scrollIntoView = () => {};
    topic.focus = () => {
        state.activeElement = topic;
        documentTarget.dispatchEvent(new FakeEvent('focusin'));
    };
    topic.blur = () => {
        state.activeElement = null;
        documentTarget.dispatchEvent(new FakeEvent('focusout'));
    };
    dialogContent.contains = (element) => element === topic;

    const document = {
        documentElement: root,
        body: createElement(['body'], state),
        scrollTop: 0,
        addEventListener: documentTarget.addEventListener,
        removeEventListener: documentTarget.removeEventListener,
        dispatchEvent: documentTarget.dispatchEvent,
    };
    Object.defineProperty(document, 'activeElement', {
        get: () => state.activeElement,
        configurable: true,
    });

    const runtime = {
        state,
        document,
        root,
        topic,
        dialogContent,
        dialogContainer,
        paper,
        frames,
        timers,
        scrollCalls,
        classes() {
            return root.classList.value;
        },
        hasClass(name) {
            return root.classList.contains(name);
        },
        styleValue(name) {
            return root.style.getPropertyValue(name);
        },
        nativeState() {
            return root.getAttribute('data-app-native-keyboard-state');
        },
        setGeometry(next, { fire = true } = {}) {
            const patch = { ...next };
            if (patch.height !== undefined && patch.rootClient === undefined) {
                patch.rootClient = patch.height;
            }
            if (patch.height !== undefined && patch.rootScroll === undefined) {
                patch.rootScroll = patch.height;
            }
            Object.assign(state, patch);
            if (fire) {
                viewportTarget.dispatchEvent(new FakeEvent('resize'));
                windowTarget.dispatchEvent(new FakeEvent('resize'));
            }
        },
        setNative(value, { event = true } = {}) {
            if (value === null) {
                delete root.attributes['data-app-native-keyboard-state'];
            } else {
                root.setAttribute('data-app-native-keyboard-state', value);
            }

            if (event) {
                windowTarget.dispatchEvent(new FakeEvent('app-native-keyboard-change'));
            }
        },
        orientation() {
            windowTarget.dispatchEvent(new FakeEvent('orientationchange'));
        },
        resize() {
            viewportTarget.dispatchEvent(new FakeEvent('resize'));
            windowTarget.dispatchEvent(new FakeEvent('resize'));
        },
        focusTopic() {
            topic.focus();
        },
        blurTopic() {
            topic.blur();
        },
        setFocusedEditor(options = {}) {
            const {
                fieldContentTop = 0,
                fieldHeight = 20,
                initialScrollTop = 0,
                scrollerTop = 0,
                scrollerHeight = 300,
                scrollerScrollHeight = 2000,
            } = options;
            const other = createElement([], state);
            other.scrollTop = 33;

            const scroller = createElement([], state);
            scroller.parentElement = document.body;
            scroller.scrollLeft = 0;
            scroller.clientHeight = scrollerHeight;
            scroller.scrollHeight = scrollerScrollHeight;
            scroller.computedOverflowY = 'auto';
            let scrollTop = initialScrollTop;
            Object.defineProperty(scroller, 'scrollTop', {
                configurable: true,
                get: () => scrollTop,
                set: (value) => {
                    const max = Math.max(0, scroller.scrollHeight - scroller.clientHeight);
                    scrollTop = Math.max(0, Math.min(value, max));
                },
            });
            scroller.getBoundingClientRect = () => ({
                top: scrollerTop,
                bottom: scrollerTop + scrollerHeight,
                left: 0,
                right: 600,
                width: 600,
                height: scrollerHeight,
            });

            const editor = createElement([], state);
            editor.parentElement = scroller;
            editor.matches = (selector) => String(selector).includes('textarea');
            editor.closest = () => null;
            editor.scrollIntoViewCalls = 0;
            editor.scrollIntoView = () => {
                editor.scrollIntoViewCalls += 1;
            };
            // Geometry follows the scroll position, as a real scroller's does.
            editor.getBoundingClientRect = () => {
                const top = fieldContentTop - scrollTop;
                return { top, bottom: top + fieldHeight, left: 0, right: 600, width: 600, height: fieldHeight };
            };
            state.activeElement = editor;
            return { editor, scroller, other };
        },
        flush(limit = 200) {
            let remaining = limit;
            while (frames.length > 0 && remaining > 0) {
                remaining -= 1;
                frames.shift()();
            }
        },
        tick() {
            runtime.flush();
        },
        advance(milliseconds, { flush = true } = {}) {
            state.now += milliseconds;
            let due = timers.filter((timer) => timer.at <= state.now).sort((a, b) => a.at - b.at);
            while (due.length > 0) {
                due.forEach((timer) => {
                    timers.splice(timers.indexOf(timer), 1);
                    timer.callback();
                });
                if (flush) {
                    runtime.flush();
                }

                due = timers.filter((timer) => timer.at <= state.now).sort((a, b) => a.at - b.at);
            }

            if (flush) {
                runtime.flush();
            }
        },
        dispatchWindow(type) {
            windowTarget.dispatchEvent(new FakeEvent(type));
        },
        dispatchDocument(type) {
            documentTarget.dispatchEvent(new FakeEvent(type));
        },
        listenerCount(type) {
            return (windowTarget.listeners.get(type) ?? []).length;
        },
    };

    Object.defineProperty(runtime, 'window', {
        get: () => windowObject,
        configurable: true,
    });

    const visualViewport = {
        get height() {
            return state.viewportHeight;
        },
        get width() {
            return state.width;
        },
        get offsetTop() {
            return state.viewportTop;
        },
        get offsetLeft() {
            return 0;
        },
        get scale() {
            return state.scale;
        },
        addEventListener: viewportTarget.addEventListener,
        removeEventListener: viewportTarget.removeEventListener,
        dispatchEvent: viewportTarget.dispatchEvent,
    };

    const windowObject = {
        get innerWidth() {
            return state.width;
        },
        get innerHeight() {
            return state.height;
        },
        get scrollX() {
            return state.scrollX ?? 0;
        },
        get scrollY() {
            return state.scrollY ?? 0;
        },
        scrollTo: (x, y) => {
            scrollCalls.push({ x, y });
            state.scrollX = x;
            state.scrollY = y;
        },
        visualViewport,
        matchMedia: (query) => (String(query).includes('pointer: coarse')
            ? { matches: state.coarse, addEventListener: () => {}, removeEventListener: () => {} }
            : { matches: false, addEventListener: () => {}, removeEventListener: () => {} }),
        performance: { now: () => state.now },
        requestAnimationFrame: (callback) => {
            frames.push(callback);
            return frames.length;
        },
        cancelAnimationFrame: () => {},
        setTimeout: (callback, delay) => {
            const id = timerId++;
            timers.push({ id, at: state.now + (delay ?? 0), callback });
            return id;
        },
        clearTimeout: (id) => {
            const index = timers.findIndex((timer) => timer.id === id);
            if (index >= 0) {
                timers.splice(index, 1);
            }
        },
        addEventListener: windowTarget.addEventListener,
        removeEventListener: windowTarget.removeEventListener,
        dispatchEvent: windowTarget.dispatchEvent,
    };

    const getComputedStyle = (element) => ({
        overflowY: element?.computedOverflowY ?? 'visible',
        paddingTop: '0px',
        paddingBottom: '0px',
    });
    runtime.getComputedStyle = getComputedStyle;

    if (options.nativeState) {
        root.setAttribute('data-app-native-keyboard-state', options.nativeState);
    }

    if (options.focused) {
        topic.focus();
    }

    const source = options.source ?? await readFile(appScriptUrl, 'utf8');
    runInNewContext(source, {
        window: windowObject,
        document,
        HTMLElement: FakeHTMLElement,
        Event: FakeEvent,
        getComputedStyle,
        console,
    });

    runtime.flush();

    return runtime;
}

let dialogModule;

export async function loadDialogModule() {
    if (!dialogModule) {
        const source = await readFile(dialogScriptUrl, 'utf8');
        dialogModule = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);
    }

    return dialogModule;
}

// The dialog module reads the browser globals on every call, so the active shell
// stays installed for the rest of the file (node:test runs one file sequentially).
function installDialogGlobals(runtime) {
    globalThis.window = runtime.window;
    globalThis.document = runtime.document;
    globalThis.HTMLElement = FakeHTMLElement;
    globalThis.getComputedStyle = runtime.getComputedStyle;
    globalThis.ResizeObserver = undefined;
    globalThis.MutationObserver = undefined;
}

export async function attachDialog(runtime, options = {}) {
    const module = await loadDialogModule();
    const paper = runtime.paper;
    const content = runtime.dialogContent;
    const container = runtime.dialogContainer;
    const form = content.form;

    paper.clientHeight = options.panelHeight ?? runtime.state.height;
    paper.offsetWidth = 600;
    paper.getBoundingClientRect = () => ({
        top: 0, bottom: paper.clientHeight, left: 0, right: 600, width: 600, height: paper.clientHeight,
    });
    paper.querySelector = () => null;

    form.scrollHeight = options.formScrollHeight ?? 0;
    form.clientHeight = options.formClientHeight ?? 120;
    form.scrollTop = options.formScrollTop ?? 0;
    runtime.topic.getBoundingClientRect = () => {
        const top = options.fieldTop ?? 40;
        const height = options.fieldHeight ?? 20;
        return { top, bottom: top + height, left: 0, right: 100, width: 100, height };
    };

    content.querySelector = (selector) => (selector === '.mud-form' ? form : null);

    installDialogGlobals(runtime);
    const attachment = module.attach(content);
    runtime.flush();
    return { attachment, paper, content, container, form };
}

export async function detachDialog(runtime, attachment) {
    const module = await loadDialogModule();
    installDialogGlobals(runtime);
    module.detach(attachment);
}
