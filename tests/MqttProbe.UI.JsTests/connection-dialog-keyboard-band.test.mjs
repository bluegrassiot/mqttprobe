import assert from 'node:assert/strict';
import { test } from 'node:test';
import { attachDialog, createShell } from './keyboard-viewport-fixture.mjs';

const bottomVariable = '--connection-dialog-keyboard-inset';
const contentKeyboardClass = 'connection-dialog-content--keyboard';
const containerKeyboardClass = 'connection-dialog-container--keyboard';

test('the dialog pads by the band the shell published, not by the raw viewport', async () => {
    const shell = await createShell({ width: 867, height: 635, viewportHeight: 337, nativeState: 'open' });
    shell.focusTopic();
    shell.tick();

    const { container, content } = await attachDialog(shell);

    assert.equal(container.style.getPropertyValue(bottomVariable), '298px');
    assert.equal(container.classList.contains(containerKeyboardClass), true);
    assert.equal(content.classList.contains(contentKeyboardClass), true);
});

test('a zero visual viewport on reopen keeps the dialog inside a positive band', async () => {
    const shell = await createShell({ width: 867, height: 635, viewportHeight: 337, nativeState: 'open' });
    shell.focusTopic();
    shell.tick();
    const { container, content } = await attachDialog(shell);

    shell.setGeometry({ width: 382, height: 644, viewportHeight: 317 });
    shell.orientation();
    shell.tick();

    shell.setGeometry({ width: 867, height: 635, viewportHeight: 0 });
    shell.orientation();
    shell.tick();

    const bottom = Number.parseInt(container.style.getPropertyValue(bottomVariable), 10);
    assert.ok(Number.isFinite(bottom), 'the dialog must publish a numeric inset');
    assert.ok(bottom >= 0 && bottom < 635, `expected an inset below the layout height, got ${bottom}`);
    assert.equal(content.classList.contains(contentKeyboardClass), true);
});

test('an open class without published geometry does not become a full-height pad', async () => {
    const shell = await createShell({ width: 867, height: 635, viewportHeight: 0, nativeState: 'open' });
    shell.focusTopic();
    shell.tick();
    shell.root.style.removeProperty('--app-keyboard-height');
    shell.root.style.removeProperty('--app-keyboard-top-inset');

    const { container, content } = await attachDialog(shell);

    assert.equal(container.style.getPropertyValue(bottomVariable), '0px');
    assert.equal(content.classList.contains(contentKeyboardClass), true);
});

test('a bootstrap frame without the shell class measures the visual viewport once', async () => {
    const shell = await createShell({ width: 867, height: 635, viewportHeight: 337, nativeState: null });
    shell.focusTopic();
    shell.tick();
    shell.root.classList.remove('app-keyboard-open');

    const { container } = await attachDialog(shell);

    assert.equal(container.style.getPropertyValue(bottomVariable), '298px');
});

test('a malformed published height never becomes a full-height pad', async () => {
    const shell = await createShell({ width: 867, height: 635, viewportHeight: 337, nativeState: 'open' });
    shell.focusTopic();
    shell.tick();
    const { container } = await attachDialog(shell);
    shell.root.style.setProperty('--app-keyboard-height', 'NaNpx');

    shell.dispatchWindow('app-keyboard-viewport-change');
    shell.tick();

    const bottom = container.style.getPropertyValue(bottomVariable);
    assert.ok(bottom === '0px' || Number.parseInt(bottom, 10) < 635, `expected no full-height pad, got '${bottom}'`);
});

test('a panned band is placed by its top edge', async () => {
    const shell = await createShell({
        width: 867, height: 635, viewportHeight: 300, viewportTop: 200, nativeState: 'open',
    });
    shell.focusTopic();
    shell.tick();

    const { container } = await attachDialog(shell);

    assert.equal(container.style.getPropertyValue('--connection-dialog-viewport-top-inset'), '200px');
    assert.equal(container.style.getPropertyValue(bottomVariable), '135px');
});

test('an oversized viewport is bounded by the layout instead of going negative', async () => {
    const shell = await createShell({ width: 867, height: 635, viewportHeight: 9000, nativeState: 'open' });
    shell.focusTopic();
    shell.tick();

    const { container } = await attachDialog(shell);

    assert.equal(container.style.getPropertyValue(bottomVariable), '0px');
});

test('a retained band keeps the focused field from panning the form', async () => {
    const shell = await createShell({ width: 867, height: 635, viewportHeight: 337, nativeState: 'open' });
    shell.focusTopic();
    shell.tick();
    shell.setGeometry({ viewportHeight: 0 });
    shell.advance(50);

    const { form, container } = await attachDialog(shell, {
        formScrollHeight: 800,
        formClientHeight: 200,
        formScrollTop: 40,
        fieldTop: 40,
        fieldHeight: 20,
    });
    shell.advance(100);

    assert.equal(container.style.getPropertyValue(bottomVariable), '298px');
    assert.equal(form.scrollTop, 40, 'a field inside the retained band must not scroll');
    assert.equal(shell.document.scrollTop, 0);
});

test('a field below the band scrolls the form, not the root', async () => {
    const shell = await createShell({ width: 867, height: 635, viewportHeight: 337, nativeState: 'open' });
    shell.focusTopic();
    shell.tick();
    shell.setGeometry({ viewportHeight: 0 });
    shell.advance(50);

    const { form } = await attachDialog(shell, {
        formScrollHeight: 800,
        formClientHeight: 200,
        formScrollTop: 40,
        fieldTop: 400,
        fieldHeight: 20,
    });
    shell.advance(100);

    assert.ok(form.scrollTop > 40, 'a field below the band must scroll its owner');
    assert.equal(shell.document.scrollTop, 0, 'the root must not scroll');
});

test('the band stays for the release delay and then clears', async () => {
    const shell = await createShell({ width: 867, height: 635, viewportHeight: 337, nativeState: 'open' });
    shell.focusTopic();
    shell.tick();
    const { container, content } = await attachDialog(shell);
    assert.equal(container.classList.contains(containerKeyboardClass), true);

    shell.setNative('closed');
    shell.advance(100);
    assert.equal(container.classList.contains(containerKeyboardClass), true, 'the tap must still land');

    shell.advance(300);
    assert.equal(container.classList.contains(containerKeyboardClass), false);
    assert.equal(content.classList.contains(contentKeyboardClass), false);
    assert.equal(container.style.getPropertyValue(bottomVariable), '0px');
});
