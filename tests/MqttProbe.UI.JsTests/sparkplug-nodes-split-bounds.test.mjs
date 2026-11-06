import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';

const modulePath = new URL('../../src/MqttProbe.UI/wwwroot/js/sparkplug-nodes-split-bounds.js', import.meta.url);
const source = await readFile(modulePath, 'utf8');
const bounds = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

function createPanel(id = 'panel') {
    return { id, isConnected: true };
}

function createWrapper(panel) {
    return { querySelector: () => panel };
}

function createRuntime(panel, position = 320) {
    const calls = [];
    return {
        calls,
        container: panel,
        firstPanel: {},
        secondPanel: {},
        divider: {},
        getDividerPosition() {
            calls.push('get');
            return position;
        },
        resetSizes() {
            calls.push('reset');
        },
        _getContainerSize() {
            return 1000;
        },
        _setPanelSizes() {
        },
    };
}

test('ensureSized returns false when the split runtime is missing', () => {
    const panel = createPanel();
    globalThis.window = { splitPanels: {} };

    assert.equal(bounds.ensureSized(createWrapper(panel)), false);
});

test('ensureSized returns false when the runtime belongs to a replaced panel', () => {
    const panel = createPanel();
    const previousPanel = createPanel();
    const runtime = createRuntime(previousPanel);
    globalThis.window = { splitPanels: { [panel.id]: runtime } };

    assert.equal(bounds.ensureSized(createWrapper(panel)), false);
    assert.deepEqual(runtime.calls, []);
});

test('ensureSized returns false when the panel is detached', () => {
    const panel = createPanel();
    panel.isConnected = false;
    const runtime = createRuntime(panel);
    globalThis.window = { splitPanels: { [panel.id]: runtime } };

    assert.equal(bounds.ensureSized(createWrapper(panel)), false);
    assert.deepEqual(runtime.calls, []);
});

test('ensureSized returns false for a zero measurement and does not reset', () => {
    const panel = createPanel();
    const runtime = createRuntime(panel, 0);
    globalThis.window = { splitPanels: { [panel.id]: runtime } };

    assert.equal(bounds.ensureSized(createWrapper(panel)), false);
    assert.deepEqual(runtime.calls, ['get']);
});

test('ensureSized measures and resets the same live runtime', () => {
    const panel = createPanel();
    const runtime = createRuntime(panel);
    globalThis.window = { splitPanels: { [panel.id]: runtime } };

    assert.equal(bounds.ensureSized(createWrapper(panel)), true);
    assert.deepEqual(runtime.calls, ['get', 'reset']);
});
