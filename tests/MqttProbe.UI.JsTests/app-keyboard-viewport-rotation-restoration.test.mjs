import assert from 'node:assert/strict';
import { test } from 'node:test';
import { createShell } from './keyboard-viewport-fixture.mjs';

const keyboardClass = 'app-keyboard-open';
const heightVariable = '--app-keyboard-height';
const topVariable = '--app-keyboard-top-inset';

async function focusedPlainBrowser() {
    const shell = await createShell({ width: 400, height: 800, viewportHeight: 800, nativeState: null });
    shell.focusTopic();
    shell.tick();
    shell.advance(200);
    shell.setGeometry({ height: 500, viewportHeight: 500 });
    shell.advance(200);
    assert.equal(shell.hasClass(keyboardClass), true, 'the same-width shrink must open the episode');
    return shell;
}

function rotate(shell, width, height, order = 'resize-first') {
    if (order === 'orientation-first') {
        shell.orientation();
        shell.tick();
        shell.setGeometry({ width, height, viewportHeight: height });
    } else {
        shell.setGeometry({ width, height, viewportHeight: height });
        shell.orientation();
    }

    shell.advance(400);
    shell.advance(200);
}

test('plain-browser rotation restores only after observed growth at the new posture', async () => {
    for (const order of ['resize-first', 'orientation-first']) {
        const shell = await focusedPlainBrowser();
        rotate(shell, 800, 250, order);

        assert.equal(shell.hasClass(keyboardClass), true, order);
        assert.equal(shell.styleValue(heightVariable), '250px', order);

        shell.setGeometry({ width: 800, height: 400, viewportHeight: 400 });
        shell.advance(200);

        assert.equal(shell.hasClass(keyboardClass), false, `${order}: restoration evidence must end the episode`);
        assert.equal(shell.styleValue(heightVariable), '', `${order}: the height variable must be removed`);
        assert.equal(shell.styleValue(topVariable), '', `${order}: the top inset must be removed`);

        shell.setGeometry({ height: 350, viewportHeight: 350 });
        shell.advance(200);
        assert.equal(shell.hasClass(keyboardClass), false, `${order}: a 50px drop from 400 is ordinary`);
        shell.setGeometry({ height: 400, viewportHeight: 400 });
        shell.advance(200);

        shell.setGeometry({ height: 200, viewportHeight: 200 });
        shell.advance(200);
        assert.equal(shell.hasClass(keyboardClass), true, `${order}: the next keyboard must use the 400px baseline`);
        assert.equal(shell.styleValue(heightVariable), '200px', order);
    }
});

test('a settled short rotation stays open when no restoration growth was observed', async () => {
    const shell = await focusedPlainBrowser();
    rotate(shell, 800, 250);

    shell.resize();
    shell.advance(1000);
    assert.equal(shell.hasClass(keyboardClass), true);
    assert.equal(shell.styleValue(heightVariable), '250px');
});

test('a direct rotation to the full short layout stays open without an intermediate growth witness', async () => {
    const shell = await focusedPlainBrowser();

    shell.setGeometry({ width: 800, height: 400, viewportHeight: 400 });
    shell.orientation();
    shell.advance(400);
    shell.advance(200);

    shell.resize();
    shell.advance(1000);
    assert.equal(shell.hasClass(keyboardClass), true, 'this is the accepted no-intermediate-witness limitation');
    assert.equal(shell.styleValue(heightVariable), '400px');
});

test('growth below the occlusion threshold keeps the keyboard mode', async () => {
    const shell = await focusedPlainBrowser();
    rotate(shell, 800, 250);

    shell.setGeometry({ height: 350, viewportHeight: 350 });
    shell.advance(200);
    assert.equal(shell.hasClass(keyboardClass), true, '100px of growth is below the 120px signal');
    assert.equal(shell.styleValue(heightVariable), '350px');

    shell.setGeometry({ height: 400, viewportHeight: 400 });
    shell.advance(200);
    assert.equal(shell.hasClass(keyboardClass), false);
});

test('invalid viewport reports do not poison the restoration witness', async () => {
    const shell = await focusedPlainBrowser();
    rotate(shell, 800, 250);

    for (const broken of [0, -40, Number.NaN, Number.POSITIVE_INFINITY, 9000]) {
        shell.setGeometry({ viewportHeight: broken });
        shell.advance(200);
        assert.equal(shell.hasClass(keyboardClass), true, `viewport ${broken} must not settle or poison`);
    }

    shell.setGeometry({ height: 400, viewportHeight: 400 });
    shell.advance(200);
    assert.equal(shell.hasClass(keyboardClass), false, 'the valid growth still restores');
});

test('native open and unknown keep their authoritative episodes on the same measurements', async () => {
    for (const native of ['open', 'unknown']) {
        const shell = await createShell({ width: 400, height: 800, viewportHeight: 800, nativeState: native });
        shell.focusTopic();
        shell.tick();
        shell.advance(200);
        shell.setGeometry({ height: 500, viewportHeight: 500 });
        shell.advance(200);
        assert.equal(shell.hasClass(keyboardClass), true, native);

        rotate(shell, 800, 250);
        assert.equal(shell.hasClass(keyboardClass), true, native);

        shell.setGeometry({ height: 400, viewportHeight: 400 });
        shell.advance(200);
        assert.equal(shell.hasClass(keyboardClass), true, `${native} must not use the plain-browser witness`);
    }
});

test('a focused rotation without a keyboard episode stays closed', async () => {
    const shell = await createShell({ width: 400, height: 800, viewportHeight: 800, nativeState: null });
    shell.focusTopic();
    shell.tick();
    shell.advance(200);

    shell.setGeometry({ width: 800, height: 600, viewportHeight: 600 });
    shell.orientation();
    shell.advance(400);
    shell.advance(200);

    assert.equal(shell.hasClass(keyboardClass), false);
    assert.equal(shell.styleValue(heightVariable), '');
});

test('a zoomed frame cannot seed the restoration witness', async () => {
    const shell = await focusedPlainBrowser();
    shell.setGeometry({ scale: 2 });
    shell.advance(200);
    rotate(shell, 800, 250);
    assert.equal(shell.hasClass(keyboardClass), true);

    shell.setGeometry({ height: 400, viewportHeight: 400, scale: 1 });
    shell.advance(200);
    assert.equal(shell.hasClass(keyboardClass), true, 'no unzoomed witness was captured at the short posture');
});

test('ordinary same-posture full restoration is unchanged', async () => {
    const shell = await focusedPlainBrowser();

    shell.setGeometry({ height: 800, viewportHeight: 800 });
    shell.advance(200);

    assert.equal(shell.hasClass(keyboardClass), false);
    assert.equal(shell.styleValue(heightVariable), '');
});
