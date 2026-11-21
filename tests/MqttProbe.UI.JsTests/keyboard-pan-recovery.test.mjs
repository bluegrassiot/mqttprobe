import assert from 'node:assert/strict';
import { test } from 'node:test';
import { attachDialog, createShell } from './keyboard-viewport-fixture.mjs';

const keyboardClass = 'app-keyboard-open';
const heightVariable = '--app-keyboard-height';
const topVariable = '--app-keyboard-top-inset';
const bottomVariable = '--connection-dialog-keyboard-inset';

// Native diag geometry: the WK pan leaves a 635px document box while window.innerHeight
// reports 163 and the visual viewport has no height at all.
const CONTAMINATED = {
    width: 867,
    height: 163,
    rootClient: 635,
    rootScroll: 635,
    viewportHeight: 0,
    viewportTop: 472,
    scrollY: 472,
    scrollX: 17,
};

test('a panned cover attempt does not spend the reopen posture budget', async () => {
    const shell = await createShell({ width: 951, height: 635, viewportHeight: 337, nativeState: 'open' });
    shell.focusTopic();
    shell.tick();
    assert.equal(shell.styleValue(heightVariable), '337px');

    shell.setGeometry({
        width: 382, height: 635, rootClient: 635, rootScroll: 635,
        viewportHeight: 317, scrollY: 189, scrollX: 17,
    });
    shell.advance(20);
    shell.advance(300);
    assert.equal(shell.scrollCalls.length, 1, 'the cover posture gets one correction');
    assert.deepEqual(shell.scrollCalls[0], { x: 17, y: 0 });

    shell.setGeometry(CONTAMINATED);
    shell.advance(300);
    shell.advance(200);

    assert.equal(shell.styleValue(heightVariable), '317px',
        'the last unpanned band survives the contaminated geometry');
    assert.equal(shell.styleValue(topVariable), '0px');
    assert.equal(shell.scrollCalls.length, 2, 'the settled reopen posture earns its own attempt');
    assert.equal(shell.scrollCalls[1].x, 17, 'horizontal scroll is preserved');
    assert.equal(shell.scrollCalls[1].y, 0);
});

test('intermediate width callbacks do not spend or re-arm before the posture settles', async () => {
    const shell = await createShell({ width: 951, height: 635, viewportHeight: 337, nativeState: 'open' });
    shell.focusTopic();
    shell.tick();
    shell.setGeometry({ width: 382, viewportHeight: 317, scrollY: 189 });
    shell.advance(20);
    shell.advance(300);
    assert.equal(shell.scrollCalls.length, 1);

    shell.setGeometry({ width: 400, scrollY: 189 });
    shell.advance(60);
    shell.setGeometry({ width: 420, scrollY: 189 });
    shell.advance(60);
    assert.equal(shell.scrollCalls.length, 1, 'a width burst must not start a new attempt');

    shell.advance(20);
    shell.advance(300);
    assert.equal(shell.scrollCalls.length, 2, 'the settled posture earns exactly one attempt');
});

test('same-posture callbacks never replenish the correction budget', async () => {
    const shell = await createShell({ width: 951, height: 635, viewportHeight: 337, nativeState: 'open' });
    shell.focusTopic();
    shell.tick();
    shell.setGeometry({ width: 382, viewportHeight: 317, scrollY: 189 });
    shell.advance(20);
    shell.advance(300);
    assert.equal(shell.scrollCalls.length, 1);

    shell.setGeometry({ scrollY: 189 });
    shell.advance(300);
    shell.dispatchWindow('resize');
    shell.advance(200);
    shell.setNative('open');
    shell.advance(200);

    assert.equal(shell.scrollCalls.length, 1, 'only one attempt per settled posture');
});

test('recovery stays off for overflow, zoom, non-coarse pointers and a missing episode', async () => {
    const overflow = await createShell({ width: 951, height: 635, viewportHeight: 337, nativeState: 'open' });
    overflow.focusTopic();
    overflow.tick();
    overflow.setGeometry({ width: 382, rootScroll: 1000, viewportHeight: 317, scrollY: 189 });
    overflow.advance(300);
    assert.equal(overflow.scrollCalls.length, 0, 'a real overflow is not a pan to correct');

    const zoomed = await createShell({ width: 951, height: 635, viewportHeight: 337, nativeState: 'open' });
    zoomed.focusTopic();
    zoomed.tick();
    zoomed.setGeometry({ width: 382, viewportHeight: 317, scrollY: 189, scale: 2 });
    zoomed.advance(300);
    assert.equal(zoomed.scrollCalls.length, 0, 'a pinch zoom is not a keyboard pan');

    const finger = await createShell({ width: 951, height: 635, viewportHeight: 337, nativeState: 'open', coarse: false });
    finger.focusTopic();
    finger.tick();
    finger.setGeometry({ width: 382, viewportHeight: 317, scrollY: 189 });
    finger.advance(300);
    assert.equal(finger.scrollCalls.length, 0);

    const idle = await createShell({ width: 951, height: 635, viewportHeight: 635, nativeState: null });
    idle.focusTopic();
    idle.tick();
    idle.setGeometry({ width: 382, viewportHeight: 635, scrollY: 189 });
    idle.advance(300);
    assert.equal(idle.scrollCalls.length, 0);
});

test('a subpixel height never publishes a zero band', async () => {
    const shell = await createShell({ width: 867, height: 635, viewportHeight: 337, nativeState: 'open' });
    shell.focusTopic();
    shell.tick();
    assert.equal(shell.styleValue(heightVariable), '337px');

    shell.setGeometry({ viewportHeight: 0.4 });
    shell.advance(150);

    assert.equal(shell.styleValue(heightVariable), '337px', 'a subpixel report keeps the prior band');
    assert.doesNotMatch(shell.styleValue(heightVariable), /^0px$/);
});

test('the dialog derives its padding from the layout box while the window height is the panned value', async () => {
    const shell = await createShell({ width: 867, height: 163, viewportHeight: 0, nativeState: 'open' });
    shell.setGeometry({ rootClient: 635, rootScroll: 635, scrollY: 472, scrollX: 17 });
    shell.focusTopic();
    shell.tick();
    shell.root.classList.add(keyboardClass);
    shell.root.style.setProperty(heightVariable, '317px');
    shell.root.style.setProperty(topVariable, '0px');

    const { container } = await attachDialog(shell, { panelHeight: 635 });

    assert.equal(container.style.getPropertyValue(bottomVariable), '318px',
        '636 - 317 is the pad in the container frame, not zero from the contaminated height');
    assert.equal(container.style.getPropertyValue('--connection-dialog-viewport-top-inset'), '0px');
});

test('reveal waits for the pan to settle and then scrolls the nested owner only', async () => {
    const shell = await createShell({ width: 867, height: 635, viewportHeight: 337, nativeState: 'open' });
    shell.focusTopic();
    shell.tick();
    shell.setGeometry({ height: 163, viewportHeight: 0, viewportTop: 472, rootClient: 635, rootScroll: 635, scrollY: 472 });
    shell.advance(250);

    const { form } = await attachDialog(shell, {
        formScrollHeight: 800,
        formClientHeight: 200,
        formScrollTop: 40,
        fieldTop: 400,
        fieldHeight: 20,
    });
    const whilePanned = form.scrollTop;
    assert.equal(whilePanned, 40, 'reveal is suppressed while the geometry is panned');

    shell.setGeometry({ height: 635, viewportHeight: 317, viewportTop: 0, scrollY: 0, scrollX: 17, viewportTop: 0 });
    shell.advance(300);

    assert.ok(form.scrollTop > 40, 'a real below-band field still scrolls its owner');
    assert.equal(shell.document.scrollTop, 0);
});

test('the first correction waits for the posture to settle', async () => {
    const shell = await createShell({ width: 951, height: 635, viewportHeight: 337, nativeState: 'open' });
    shell.focusTopic();
    shell.tick();
    shell.setGeometry({ width: 382, height: 635, viewportHeight: 317, scrollY: 189 });

    shell.advance(1);
    assert.equal(shell.scrollCalls.length, 0);
    shell.advance(90);
    assert.equal(shell.scrollCalls.length, 0, 'an unsettled posture must not correct');

    shell.advance(200);
    assert.equal(shell.scrollCalls.length, 1);
});

test('a stale quiet callback cannot grant an attempt for an intermediate width', async () => {
    const shell = await createShell({ width: 951, height: 635, viewportHeight: 337, nativeState: 'open' });
    shell.focusTopic();
    shell.tick();
    shell.setGeometry({ width: 382, height: 635, viewportHeight: 317, scrollY: 189 });
    shell.advance(60);
    shell.setGeometry({ width: 400, scrollY: 189 });
    shell.advance(60);
    shell.setGeometry({ width: 420, scrollY: 189 });
    shell.advance(30);

    assert.equal(shell.scrollCalls.length, 0, 'the earlier timer is stale');

    shell.advance(250);
    assert.equal(shell.scrollCalls.length, 1, 'only the settled posture earns the attempt');
});

test('a raw scale that is not one blocks recovery', async () => {
    for (const bad of [Number.NaN, Number.POSITIVE_INFINITY, 0, 0.5, 1.5]) {
        const shell = await createShell({
            width: 951, height: 635, viewportHeight: 337, nativeState: 'open', scale: bad,
        });
        shell.focusTopic();
        shell.tick();
        shell.setGeometry({ width: 382, viewportHeight: 317, scrollY: 189 });
        shell.advance(400);
        assert.equal(shell.scrollCalls.length, 0, `raw scale ${bad} must block the guard`);
    }

    const real = await createShell({ width: 951, height: 635, viewportHeight: 337, nativeState: 'open', scale: 1 });
    real.focusTopic();
    real.tick();
    real.setGeometry({ width: 382, viewportHeight: 317, scrollY: 189 });
    real.advance(20);
    real.advance(400);
    assert.equal(real.scrollCalls.length, 1);
});

test('a real overflow keeps its band, padding and nested reveal when the boxes differ', async () => {
    const shell = await createShell({
        width: 867, height: 317, rootClient: 635, rootScroll: 1000, viewportHeight: 317,
        nativeState: 'open',
    });
    shell.focusTopic();
    shell.tick();
    assert.equal(shell.styleValue(heightVariable), '317px');

    shell.setGeometry({ scrollY: 60 });
    shell.advance(20);
    shell.advance(300);
    assert.equal(shell.scrollCalls.length, 0, 'an overflowing root is not a pan to correct');

    const { container, form } = await attachDialog(shell, {
        formScrollHeight: 800,
        formClientHeight: 200,
        formScrollTop: 40,
        fieldTop: 400,
        fieldHeight: 20,
    });
    shell.advance(200);

    assert.equal(container.style.getPropertyValue(bottomVariable), '0px',
        'outside the recovery frame the pad comes from the window height');
    assert.ok(form.scrollTop > 40, 'nested reveal still works outside the recovery frame');
});
test('the recovery attribute is published while settling and cleared after restore', async () => {
    const shell = await createShell({ width: 867, height: 635, viewportHeight: 337, nativeState: 'open' });
    shell.focusTopic();
    shell.tick();

    shell.setGeometry(CONTAMINATED);
    shell.advance(20);
    assert.equal(shell.root.getAttribute('data-app-keyboard-viewport-recovery'), 'settling');

    shell.setGeometry({ height: 635, viewportHeight: 317, viewportTop: 0, scrollY: 0, rootScroll: 635 });
    shell.advance(20);
    shell.advance(300);
    assert.equal(shell.root.getAttribute('data-app-keyboard-viewport-recovery'), null);
});
test('an expired recovery frame is torn down autonomously after a real overflow', async () => {
    const shell = await createShell({ width: 867, height: 635, viewportHeight: 337, nativeState: 'open' });
    shell.focusTopic();
    shell.tick();
    shell.setGeometry(CONTAMINATED);
    shell.advance(20);
    assert.equal(shell.root.getAttribute('data-app-keyboard-viewport-recovery'), 'settling');

    const { container, form } = await attachDialog(shell, {
        formScrollHeight: 800,
        formClientHeight: 200,
        formScrollTop: 40,
        fieldTop: 400,
        fieldHeight: 20,
    });
    const pannedPad = Number.parseInt(container.style.getPropertyValue(bottomVariable), 10);
    assert.ok(pannedPad <= 298, `expected the retained band pad, got ${pannedPad}`);
    const corrections = shell.scrollCalls.length;

    // Exit context with geometry only; no resize or native hint follows.
    shell.setGeometry({ height: 163, viewportHeight: 163, viewportTop: 0, scrollY: 0, rootClient: 635, rootScroll: 1000 });
    shell.advance(3000);

    assert.equal(shell.root.getAttribute('data-app-keyboard-viewport-recovery'), null,
        'metadata must expire on its own after the hold');
    assert.equal(shell.scrollCalls.length, corrections, 'expiry must not reset the root again');
    assert.equal(container.style.getPropertyValue(bottomVariable), '0px',
        'the dialog returns to the window frame after expiry');
    assert.ok(form.scrollTop > 40, 'nested reveal resumes after expiry');
    assert.equal(shell.document.scrollTop, 0);
});

test('metadata also expires when the context becomes a zoom', async () => {
    const shell = await createShell({ width: 867, height: 163, viewportHeight: 0, nativeState: 'open' });
    shell.focusTopic();
    shell.tick();
    shell.setGeometry({ rootClient: 635, rootScroll: 635, scrollY: 472 });
    shell.advance(20);
    assert.equal(shell.root.getAttribute('data-app-keyboard-viewport-recovery'), 'settling');

    shell.setGeometry({ height: 635, viewportHeight: 317, scale: 1.5, scrollY: 0, rootClient: 635, rootScroll: 635 });
    shell.advance(3000);

    assert.equal(shell.root.getAttribute('data-app-keyboard-viewport-recovery'), null);
});

test('an expired watchdog cannot clear a new recovery epoch', async () => {
    const shell = await createShell({ width: 867, height: 163, viewportHeight: 0, nativeState: 'open' });
    shell.focusTopic();
    shell.tick();
    shell.setGeometry({ rootClient: 635, rootScroll: 635, scrollY: 472 });
    shell.advance(20);
    assert.equal(shell.root.getAttribute('data-app-keyboard-viewport-recovery'), 'settling');

    // A new posture starts a new recovery while an old watchdog could still fire.
    shell.setGeometry({ width: 382, rootClient: 635, rootScroll: 635, scrollY: 472 });
    shell.advance(20);
    shell.advance(120);
    assert.equal(shell.root.getAttribute('data-app-keyboard-viewport-recovery'), 'settling',
        'the new epoch is not cleared by the previous watchdog');
    shell.advance(3000);
    assert.equal(shell.root.getAttribute('data-app-keyboard-viewport-recovery'), null);
});