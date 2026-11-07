import assert from 'node:assert/strict';
import { test } from 'node:test';
import { createShell } from './keyboard-viewport-fixture.mjs';

const heightVariable = '--app-keyboard-height';
const topVariable = '--app-keyboard-top-inset';
const keyboardClass = 'app-keyboard-open';

function positivePixels(runtime, name) {
    const raw = runtime.styleValue(name);
    assert.match(raw, /^\d+px$/, `${name} must stay a finite positive pixel value, got '${raw}'`);
    return Number.parseFloat(raw);
}

test('native open before the script loads is honoured without a geometry event', async () => {
    const shell = await createShell({ width: 867, height: 635, viewportHeight: 635, nativeState: 'open' });

    assert.equal(shell.hasClass(keyboardClass), true);
    assert.equal(positivePixels(shell, heightVariable), 635);
    assert.equal(positivePixels(shell, topVariable), 0);
});

test('a native open event after load applies the class from a shrunk layout', async () => {
    const shell = await createShell({ width: 951, height: 669, viewportHeight: 669, nativeState: 'closed' });
    shell.focusTopic();
    shell.tick();
    assert.equal(shell.hasClass(keyboardClass), false);

    shell.setGeometry({ height: 348, viewportHeight: 348 }, { fire: false });
    shell.setNative('open');
    shell.advance(20);

    assert.equal(shell.hasClass(keyboardClass), true);
    assert.equal(positivePixels(shell, heightVariable), 348);
});

test('overlay keyboard survives the fold and keeps a bounded band when the reopen reports zero', async () => {
    const shell = await createShell({ width: 867, height: 635, viewportHeight: 337, nativeState: 'open' });
    shell.focusTopic();
    shell.tick();
    assert.equal(shell.hasClass(keyboardClass), true);
    assert.equal(positivePixels(shell, heightVariable), 337);

    shell.setGeometry({ width: 382, height: 644, viewportHeight: 317 });
    shell.orientation();
    shell.tick();
    assert.equal(shell.hasClass(keyboardClass), true, 'a fold is not a dismissal');
    assert.equal(positivePixels(shell, heightVariable), 317);

    shell.setGeometry({ width: 867, height: 635, viewportHeight: 0 });
    shell.orientation();
    shell.tick();

    assert.equal(shell.hasClass(keyboardClass), true);
    const band = positivePixels(shell, heightVariable);
    assert.ok(band > 0 && band <= 635, `expected the retained band to be bounded by the layout, got ${band}`);
    assert.equal(positivePixels(shell, topVariable), 0);
});

test('shrink-mode fold keeps the keyboard class in both event orders', async () => {
    for (const order of ['resize-first', 'orientation-first']) {
        const shell = await createShell({ width: 951, height: 669, viewportHeight: 669, nativeState: 'open' });
        shell.focusTopic();
        shell.tick();
        shell.setGeometry({ height: 348, viewportHeight: 348 });
        shell.tick();
        assert.equal(positivePixels(shell, heightVariable), 348);

        if (order === 'orientation-first') {
            shell.orientation();
            shell.tick();
            shell.setGeometry({ width: 466, height: 317, viewportHeight: 317 });
        } else {
            shell.setGeometry({ width: 466, height: 317, viewportHeight: 317 });
            shell.orientation();
        }

        shell.tick();
        assert.equal(shell.hasClass(keyboardClass), true, order);
        assert.equal(positivePixels(shell, heightVariable), 317, order);

        shell.setGeometry({ width: 951, height: 348, viewportHeight: 348 });
        shell.tick();
        assert.equal(shell.hasClass(keyboardClass), true, order);
        assert.equal(positivePixels(shell, heightVariable), 348, order);
    }
});

test('a fold does not promote the shrunken height to the idle baseline', async () => {
    const shell = await createShell({ width: 951, height: 669, viewportHeight: 669, nativeState: 'open' });
    shell.focusTopic();
    shell.tick();
    shell.setGeometry({ height: 348, viewportHeight: 348 });
    shell.tick();
    shell.setGeometry({ width: 466, height: 317, viewportHeight: 317 });
    shell.orientation();
    shell.tick();

    shell.setNative('closed');
    shell.advance(20);
    shell.blurTopic();
    shell.setGeometry({ width: 466, height: 678, viewportHeight: 678 });
    shell.advance(200);
    assert.equal(shell.hasClass(keyboardClass), false);
    assert.equal(shell.styleValue(heightVariable), '');

    shell.setNative(null);
    shell.focusTopic();
    shell.setGeometry({ height: 348, viewportHeight: 348 });
    shell.advance(200);
    assert.equal(shell.hasClass(keyboardClass), true, 'the idle baseline must still see the next keyboard');
    assert.equal(positivePixels(shell, heightVariable), 348);
});

test('a plain browser rotation with a focused field does not invent a keyboard', async () => {
    const shell = await createShell({ width: 951, height: 669, viewportHeight: 669, nativeState: null });
    shell.focusTopic();
    shell.tick();
    assert.equal(shell.hasClass(keyboardClass), false);

    shell.setGeometry({ width: 466, height: 669, viewportHeight: 669 });
    shell.orientation();
    shell.tick();
    assert.equal(shell.hasClass(keyboardClass), false);
    assert.equal(shell.styleValue(heightVariable), '');
});

test('a transient zero, negative, non-finite or oversized viewport never publishes a broken band', async () => {
    for (const broken of [0, -40, Number.NaN, Number.POSITIVE_INFINITY, 9000]) {
        const shell = await createShell({ width: 867, height: 635, viewportHeight: 337, nativeState: 'open' });
        shell.focusTopic();
        shell.tick();

        shell.setGeometry({ viewportHeight: broken });
        shell.advance(200);

        assert.equal(shell.hasClass(keyboardClass), true, `viewport ${broken} is not a dismissal`);
        const band = positivePixels(shell, heightVariable);
        assert.ok(band > 0 && band <= 635, `viewport ${broken} produced ${band}`);
        positivePixels(shell, topVariable);
    }
});

test('a floating keyboard leaves the whole view visible without a bottom inset', async () => {
    const shell = await createShell({ width: 867, height: 635, viewportHeight: 635, nativeState: 'open' });
    shell.focusTopic();
    shell.tick();

    assert.equal(shell.hasClass(keyboardClass), true);
    assert.equal(positivePixels(shell, heightVariable), 635);
    assert.equal(positivePixels(shell, topVariable), 0);
});

test('native unknown preserves an established episode but does not start one', async () => {
    const shell = await createShell({ width: 867, height: 635, viewportHeight: 337, nativeState: 'open' });
    shell.focusTopic();
    shell.tick();
    assert.equal(shell.hasClass(keyboardClass), true);

    shell.setNative('unknown');
    shell.setGeometry({ viewportHeight: 0 });
    shell.advance(200);
    assert.equal(shell.hasClass(keyboardClass), true, 'unknown is ambiguous, not a dismissal');
    assert.ok(positivePixels(shell, heightVariable) > 0);

    const idle = await createShell({ width: 867, height: 635, viewportHeight: 635, nativeState: 'unknown' });
    idle.focusTopic();
    idle.advance(300);
    assert.equal(idle.hasClass(keyboardClass), false);
});

test('native closed removes the class and the geometry', async () => {
    const shell = await createShell({ width: 867, height: 635, viewportHeight: 337, nativeState: 'open' });
    shell.focusTopic();
    shell.tick();

    shell.setNative('closed');
    shell.advance(50);

    assert.equal(shell.hasClass(keyboardClass), false);
    assert.equal(shell.styleValue(heightVariable), '');
    assert.equal(shell.styleValue(topVariable), '');
});

test('the viewport change event fires only when the effective band changes', async () => {
    const shell = await createShell({ width: 867, height: 635, viewportHeight: 337, nativeState: 'open' });
    let events = 0;
    shell.window.addEventListener('app-keyboard-viewport-change', () => {
        events += 1;
    });

    shell.focusTopic();
    shell.tick();
    shell.resize();
    shell.tick();
    shell.advance(200);
    assert.equal(events, 0, 'an unchanged band must not notify the dialog');

    shell.setNative('closed');
    shell.advance(50);
    assert.equal(events, 1);
});

test('a hidden render layout of zero keeps the class and the last published band', async () => {
    const shell = await createShell({ width: 867, height: 635, viewportHeight: 337, nativeState: 'open' });
    shell.focusTopic();
    shell.tick();
    assert.equal(shell.styleValue(heightVariable), '337px');

    shell.setGeometry({ height: 0, viewportHeight: 0, rootClient: 0, rootScroll: 0 });
    shell.advance(200);

    assert.equal(shell.hasClass(keyboardClass), true, 'a zero layout is not a dismissal');
    assert.equal(shell.styleValue(heightVariable), '337px', 'the last band must not be replaced by zero');
    assert.doesNotMatch(shell.styleValue(heightVariable), /NaN/);

    shell.setGeometry({ height: 635, viewportHeight: 337, rootClient: 635, rootScroll: 635 });
    shell.advance(100);
    assert.equal(shell.styleValue(heightVariable), '337px');
});

test('a native closed during the display handoff keeps the band for the reopen', async () => {
    const shell = await createShell({ width: 382, height: 644, viewportHeight: 317, nativeState: 'open' });
    shell.focusTopic();
    shell.tick();
    assert.equal(shell.styleValue(heightVariable), '317px');

    shell.setNative('closed');
    shell.advance(50);
    assert.equal(shell.hasClass(keyboardClass), false, 'the visible keyboard is gone');
    assert.equal(shell.styleValue(heightVariable), '');

    shell.setNative('unknown');
    shell.advance(100);
    assert.equal(shell.hasClass(keyboardClass), false, 'an ambiguous frame at the same posture is not recovery evidence');
    shell.setNative('unknown');
    shell.advance(100);
    assert.equal(shell.hasClass(keyboardClass), false);

    shell.setNative('unknown', { event: false });
    shell.setGeometry({ width: 867, height: 635, rootClient: 635, rootScroll: 635, viewportHeight: 0 });
    shell.advance(200);

    assert.equal(shell.hasClass(keyboardClass), true, 'the unfinished transition still recovers');
    assert.equal(shell.styleValue(heightVariable), '317px');
    assert.equal(shell.styleValue(topVariable), '0px');
});

test('a width change before the handoff close still recovers', async () => {
    const shell = await createShell({ width: 951, height: 669, viewportHeight: 337, nativeState: 'open' });
    shell.focusTopic();
    shell.tick();
    shell.setGeometry({ width: 382, height: 644, rootClient: 644, rootScroll: 644, viewportHeight: 317 });
    shell.advance(50);
    shell.setNative('closed');
    shell.advance(50);

    shell.setGeometry({ width: 867, height: 635, rootClient: 635, rootScroll: 635, viewportHeight: 0 });
    shell.setNative('unknown');
    shell.advance(200);

    assert.equal(shell.hasClass(keyboardClass), true);
    assert.equal(shell.styleValue(heightVariable), '317px');
});

test('a closed with restored geometry commits the end and never revives', async () => {
    const shell = await createShell({ width: 951, height: 669, viewportHeight: 337, nativeState: 'open' });
    shell.focusTopic();
    shell.tick();

    shell.setNative('closed');
    shell.setGeometry({ viewportHeight: 669 });
    shell.advance(50);
    assert.equal(shell.hasClass(keyboardClass), false);
    assert.equal(shell.styleValue(heightVariable), '');

    shell.setNative('unknown', { event: false });
    shell.setGeometry({ width: 466, height: 669, rootClient: 669, rootScroll: 669, viewportHeight: 0 });
    shell.advance(200);
    assert.equal(shell.hasClass(keyboardClass), false, 'a committed end must not revive');
    assert.equal(shell.styleValue(heightVariable), '');

    shell.setNative('unknown');
    shell.advance(200);
    assert.equal(shell.hasClass(keyboardClass), false);
    assert.equal(shell.styleValue(heightVariable), '');
});

test('an invalid measurement after the handoff close cannot commit the end', async () => {
    const shell = await createShell({ width: 382, height: 644, viewportHeight: 317, nativeState: 'open' });
    shell.focusTopic();
    shell.tick();
    assert.equal(shell.styleValue(heightVariable), '317px');

    shell.setNative('closed');
    shell.advance(50);
    assert.equal(shell.hasClass(keyboardClass), false);

    shell.setGeometry({ viewportHeight: 9000 });
    shell.advance(100);
    assert.equal(shell.hasClass(keyboardClass), false, 'a broken measurement does not reopen by itself');
    assert.equal(shell.styleValue(heightVariable), '');

    shell.setNative('unknown', { event: false });
    shell.setGeometry({ width: 867, height: 635, rootClient: 635, rootScroll: 635, viewportHeight: 0 });
    shell.advance(200);

    assert.equal(shell.hasClass(keyboardClass), true, 'the checkpoint survived the invalid measurement');
    assert.equal(shell.styleValue(heightVariable), '317px');
});

test('a full shorter layout commits the dismissal and never revives', async () => {
    const shell = await createShell({ width: 951, height: 669, viewportHeight: 337, nativeState: 'open' });
    shell.focusTopic();
    shell.tick();

    shell.setGeometry({ width: 867, height: 400, rootClient: 400, rootScroll: 400, viewportHeight: 400 });
    shell.setNative('closed');
    shell.advance(100);
    assert.equal(shell.hasClass(keyboardClass), false);
    assert.equal(shell.styleValue(heightVariable), '');

    shell.setNative('unknown', { event: false });
    shell.setGeometry({ width: 466, height: 400, rootClient: 400, rootScroll: 400, viewportHeight: 0 });
    shell.advance(200);

    assert.equal(shell.hasClass(keyboardClass), false,
        'a full viewport at the current layout is trustworthy dismissal evidence');
    assert.equal(shell.styleValue(heightVariable), '');
});

test('a closed at a shorter restored layout commits the end', async () => {
    const shell = await createShell({ width: 951, height: 669, viewportHeight: 337, nativeState: 'open' });
    shell.focusTopic();
    shell.tick();

    shell.setGeometry({ width: 867, height: 635, rootClient: 635, rootScroll: 635, viewportHeight: 635 });
    shell.setNative('closed');
    shell.advance(100);
    assert.equal(shell.hasClass(keyboardClass), false);
    assert.equal(shell.styleValue(heightVariable), '');

    shell.setNative('unknown', { event: false });
    shell.setGeometry({ width: 466, height: 635, viewportHeight: 0 });
    shell.advance(200);
    assert.equal(shell.hasClass(keyboardClass), false, 'a shorter but restored layout still commits');
});

test('an oversized visual viewport is rejected before it becomes the retained band', async () => {
    const shell = await createShell({ width: 867, height: 635, viewportHeight: 337, nativeState: 'open' });
    shell.focusTopic();
    shell.tick();
    assert.equal(shell.styleValue(heightVariable), '337px');

    shell.setGeometry({ viewportHeight: 9000 });
    shell.advance(100);
    assert.equal(shell.styleValue(heightVariable), '337px', 'an oversized viewport is not a measurement');

    shell.setGeometry({ viewportHeight: 0 });
    shell.advance(100);
    assert.equal(shell.styleValue(heightVariable), '337px');
});

test('an out-of-bounds band measurement is rejected', async () => {
    const shell = await createShell({ width: 867, height: 635, viewportHeight: 337, nativeState: 'open' });
    shell.focusTopic();
    shell.tick();

    shell.setGeometry({ viewportTop: -40, viewportHeight: 100 });
    shell.advance(100);
    assert.equal(shell.styleValue(heightVariable), '337px', 'a negative offset is not a usable measurement');

    shell.setGeometry({ viewportTop: 600, viewportHeight: 100 });
    shell.advance(100);
    assert.equal(shell.styleValue(heightVariable), '337px', 'a band past the layout is not usable');
});

test('valid panned and zoomed measurements are still accepted', async () => {
    const panned = await createShell({ width: 867, height: 635, viewportHeight: 300, viewportTop: 200, nativeState: 'open' });
    panned.focusTopic();
    panned.tick();
    assert.equal(panned.styleValue(heightVariable), '300px');
    assert.equal(panned.styleValue(topVariable), '200px');

    const zoomed = await createShell({ width: 867, height: 635, viewportHeight: 317, scale: 2, nativeState: 'open' });
    zoomed.focusTopic();
    zoomed.tick();
    assert.equal(zoomed.styleValue(heightVariable), '317px');
});

test('an invalid viewport during a fold does not scroll the root', async () => {
    const shell = await createShell({ width: 867, height: 635, viewportHeight: 337, nativeState: 'open' });
    shell.focusTopic();
    shell.tick();
    shell.scrollCalls.length = 0;

    shell.setGeometry({ width: 382, height: 644, rootClient: 644, rootScroll: 644, viewportHeight: 317 });
    shell.advance(50);
    shell.setGeometry({ width: 867, height: 635, rootClient: 635, rootScroll: 635, viewportHeight: 0 });
    shell.advance(200);

    assert.equal(shell.scrollCalls.length, 0, 'no root scroll is forced while the band is retained');
    assert.equal(shell.hasClass(keyboardClass), true);
});

test('the shell is the only writer of the class when the native attribute disagrees', async () => {
    const shell = await createShell({ width: 867, height: 635, viewportHeight: 337, nativeState: 'open' });
    shell.focusTopic();
    shell.tick();

    shell.setNative('closed');
    shell.advance(50);
    shell.root.classList.add(keyboardClass);

    shell.resize();
    shell.tick();
    assert.equal(shell.hasClass(keyboardClass), false, 'a stray class is corrected on the next pass');
});
