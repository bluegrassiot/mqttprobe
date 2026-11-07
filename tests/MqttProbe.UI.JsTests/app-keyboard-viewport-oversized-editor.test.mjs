import assert from 'node:assert/strict';
import { test } from 'node:test';
import { createShell } from './keyboard-viewport-fixture.mjs';

const bandTop = 8;
const bandBottom = 292;

async function shellWithEditor(options) {
    const shell = await createShell({ width: 867, height: 300, viewportHeight: 300, nativeState: 'open' });
    shell.advance(200);
    const scenario = shell.setFocusedEditor(options);
    shell.resize();
    shell.tick();
    return { shell, ...scenario };
}

function assertRootAndSiblingsUntouched(shell, scroller, other) {
    assert.equal(shell.scrollCalls.length, 0, 'the root must not be scrolled');
    assert.equal(shell.window.scrollX, 0);
    assert.equal(shell.window.scrollY, 0);
    assert.equal(scroller.scrollLeft, 0, 'the reveal is vertical only');
    assert.equal(other.scrollTop, 33, 'another nested scroller must keep its position');
}

test('an oversized editor below the band aligns to the band top and stays there', async () => {
    const { shell, editor, scroller, other } = await shellWithEditor({
        fieldContentTop: 50,
        fieldHeight: 400,
    });

    assert.equal(scroller.scrollTop, 50 - bandTop, 'the first pass must align the oversized editor top');
    shell.advance(200);
    assert.equal(scroller.scrollTop, 50 - bandTop, 'the second pass must not reverse the first');

    shell.resize();
    shell.tick();
    shell.advance(200);
    assert.equal(scroller.scrollTop, 50 - bandTop, 'later passes must keep the same top-aligned edge');
    assert.equal(editor.scrollIntoViewCalls, 0, 'a usable scroller must not fall back to scrollIntoView');
    assertRootAndSiblingsUntouched(shell, scroller, other);
});

test('an oversized editor above the band aligns to the band top and stays there', async () => {
    const { shell, editor, scroller, other } = await shellWithEditor({
        fieldContentTop: 50,
        fieldHeight: 400,
        initialScrollTop: 100,
    });

    assert.equal(scroller.scrollTop, 50 - bandTop, 'an above-band oversized editor also aligns to the top');
    shell.advance(200);
    assert.equal(scroller.scrollTop, 50 - bandTop, 'the second pass must not reverse the first');

    shell.resize();
    shell.tick();
    shell.advance(200);
    assert.equal(scroller.scrollTop, 50 - bandTop);
    assert.equal(editor.scrollIntoViewCalls, 0);
    assertRootAndSiblingsUntouched(shell, scroller, other);
});

test('an ordinary editor below or above the band still scrolls by the nearest edge', async () => {
    const below = await shellWithEditor({ fieldContentTop: 350, fieldHeight: 20 });
    assert.equal(below.scroller.scrollTop, 370 - bandBottom);
    below.shell.advance(200);
    assert.equal(below.scroller.scrollTop, 370 - bandBottom);
    assert.equal(below.editor.scrollIntoViewCalls, 0);
    assertRootAndSiblingsUntouched(below.shell, below.scroller, below.other);

    const above = await shellWithEditor({ fieldContentTop: 50, fieldHeight: 20, initialScrollTop: 100 });
    assert.equal(above.scroller.scrollTop, 50 - bandTop);
    above.shell.advance(200);
    assert.equal(above.scroller.scrollTop, 50 - bandTop);
    assert.equal(above.editor.scrollIntoViewCalls, 0);
    assertRootAndSiblingsUntouched(above.shell, above.scroller, above.other);
});

test('an already visible editor keeps the scroller position', async () => {
    const { shell, editor, scroller, other } = await shellWithEditor({
        fieldContentTop: 100,
        fieldHeight: 20,
    });

    assert.equal(scroller.scrollTop, 0);
    shell.advance(200);
    assert.equal(scroller.scrollTop, 0);
    assert.equal(editor.scrollIntoViewCalls, 0);
    assertRootAndSiblingsUntouched(shell, scroller, other);
});
