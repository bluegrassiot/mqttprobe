import assert from 'node:assert/strict';
import { test } from 'node:test';
import { createShell } from './keyboard-viewport-fixture.mjs';

const keyboardClass = 'app-keyboard-open';
const heightVariable = '--app-keyboard-height';
const topVariable = '--app-keyboard-top-inset';

async function openWithEditor(options) {
    const shell = await createShell({
        width: 400,
        height: 800,
        viewportHeight: options.viewportHeight ?? 300,
        viewportTop: options.viewportTop ?? 0,
        nativeState: 'open',
    });
    shell.focusTopic();
    shell.tick();
    shell.advance(200);

    const scenario = shell.setFocusedEditor(options);
    shell.resize();
    shell.tick();
    shell.advance(200);
    scenario.scroller.scrollTop = 0;
    return { shell, ...scenario };
}

function assertNoStaleScroll(shell, editor, scroller, other) {
    assert.equal(editor.scrollIntoViewCalls, 0);
    assert.equal(scroller.scrollLeft, 0);
    assert.equal(other.scrollTop, 33);
    assert.equal(shell.window.scrollX, 0);
    assert.equal(shell.window.scrollY, 0);
}

test('a deferred reveal cannot scroll after the native close removed the keyboard state', async () => {
    const { shell, editor, scroller, other } = await openWithEditor({ fieldContentTop: 350 });

    shell.resize();
    shell.tick();
    assert.ok(scroller.scrollTop > 0, 'the queued pass must start from a meaningful scroll position');

    shell.setNative('closed');
    shell.advance(50);
    assert.equal(shell.hasClass(keyboardClass), false);
    assert.equal(shell.styleValue(heightVariable), '');

    scroller.scrollTop = 25;
    shell.advance(200);
    assert.equal(scroller.scrollTop, 25, 'the stale callback must not move the user position');
    assertNoStaleScroll(shell, editor, scroller, other);
});

test('a deferred reveal from a smaller band cannot override the current larger band', async () => {
    const { shell, editor, scroller, other } = await openWithEditor({
        fieldContentTop: 350,
        scrollerHeight: 500,
    });

    shell.resize();
    shell.tick();
    shell.setGeometry({ viewportHeight: 500 });
    shell.tick();
    assert.equal(shell.styleValue(heightVariable), '500px');

    scroller.scrollTop = 0;
    shell.advance(200);
    assert.equal(scroller.scrollTop, 0, 'the old 300px band must not push the field');
    assertNoStaleScroll(shell, editor, scroller, other);
});

test('a top-inset-only change rejects the old band signature', async () => {
    const { shell, editor, scroller, other } = await openWithEditor({
        viewportHeight: 300,
        viewportTop: 100,
        fieldContentTop: 150,
    });

    shell.resize();
    shell.tick();
    scroller.scrollTop = 100;

    shell.setGeometry({ viewportTop: 0 });
    shell.tick();
    assert.equal(shell.styleValue(topVariable), '0px');
    assert.equal(shell.styleValue(heightVariable), '300px');

    shell.advance(200);
    assert.equal(scroller.scrollTop, 100, 'the old top inset must not move the field');
    assertNoStaleScroll(shell, editor, scroller, other);
});

test('a callback whose band still matches the published state still reveals', async () => {
    const { shell, editor, scroller, other } = await openWithEditor({ fieldContentTop: 350 });

    shell.resize();
    shell.tick();
    assert.equal(scroller.scrollTop, 370 - 292, 'the current RAF callback must reveal the field');

    shell.advance(150);
    assert.equal(scroller.scrollTop, 370 - 292, 'the matching timer callback must stay idempotent');
    assertNoStaleScroll(shell, editor, scroller, other);
});
