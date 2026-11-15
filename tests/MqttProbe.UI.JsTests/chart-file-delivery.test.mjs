import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';

const modulePath = new URL('../../src/MqttProbe.UI/wwwroot/js/chart-file-delivery.js', import.meta.url);
const source = await readFile(modulePath, 'utf8');
const { downloadFile } = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

function installBrowser(options = {}) {
    const originalDocument = globalThis.document;
    const originalUrl = globalThis.URL;
    const record = {
        events: [],
        appended: [],
        removed: [],
        blobs: [],
        urls: [],
        revoked: [],
        anchors: [],
        tagName: null,
    };
    const anchor = {
        parentNode: null,
        href: '',
        download: '',
        click() {
            record.events.push('click');
            if (options.onClick) {
                options.onClick(anchor);
            }
        },
        remove() {
            record.events.push('remove');
            if (options.onRemove) {
                options.onRemove(this);
            }
            record.removed.push(this);
            this.parentNode = null;
        },
    };
    const body = {
        appendChild(node) {
            record.events.push('append');
            if (options.onAppend) {
                options.onAppend(node);
            }
            record.appended.push(node);
            node.parentNode = body;
            return node;
        },
        removeChild(node) {
            record.events.push('remove');
            record.removed.push(node);
            node.parentNode = null;
            return node;
        },
    };

    globalThis.document = {
        body,
        createElement(tagName) {
            record.tagName = tagName;
            record.anchors.push(anchor);
            if (options.onCreateElement) {
                options.onCreateElement(anchor);
            }
            return anchor;
        },
    };
    globalThis.URL = {
        createObjectURL(blob) {
            record.events.push('create');
            record.blobs.push(blob);
            if (options.onCreateObjectUrl) {
                options.onCreateObjectUrl(blob);
            }
            const url = `blob:test/${record.blobs.length}`;
            record.urls.push(url);
            return url;
        },
        revokeObjectURL(url) {
            record.events.push('revoke');
            record.revoked.push(url);
            if (options.onRevoke) {
                options.onRevoke(url);
            }
        },
    };

    return {
        record,
        anchor,
        restore() {
            globalThis.document = originalDocument;
            globalThis.URL = originalUrl;
        },
    };
}

async function bytesOf(blob) {
    return new Uint8Array(await blob.arrayBuffer());
}

test('string content downloads through a Blob anchor and revokes the object URL', async () => {
    const browser = installBrowser();
    try {
        const content = `\uFEFFtimestamp,utc_offset\r\n2026-11-14 07:39:59.652,+00:00\r\n`;
        await downloadFile('chart.csv', content, 'text/csv;charset=utf-8');

        const [blob] = browser.record.blobs;
        assert.ok(blob instanceof Blob);
        assert.equal(blob.type, 'text/csv;charset=utf-8');
        assert.deepEqual(
            [...await bytesOf(blob)],
            [...Buffer.from(content, 'utf8')]);
        assert.equal(browser.record.tagName, 'a');
        assert.equal(browser.anchor.download, 'chart.csv');
        assert.equal(browser.anchor.href, browser.record.urls[0]);
        assert.deepEqual(browser.record.appended, [browser.anchor]);
        assert.deepEqual(browser.record.removed, [browser.anchor]);
        assert.deepEqual(browser.record.revoked, [browser.record.urls[0]]);
        assert.equal(browser.anchor.parentNode, null);
        assert.deepEqual(
            browser.record.events,
            ['create', 'append', 'click', 'remove', 'revoke']);
    } finally {
        browser.restore();
    }
});

test('byte content keeps its exact bytes', async () => {
    const browser = installBrowser();
    try {
        const png = new Uint8Array([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
        await downloadFile('chart.png', png, 'image/png');

        assert.equal(browser.record.blobs[0].type, 'image/png');
        assert.deepEqual([...await bytesOf(browser.record.blobs[0])], [...png]);
    } finally {
        browser.restore();
    }
});

test('an ArrayBuffer is accepted without copying through a string', async () => {
    const browser = installBrowser();
    try {
        const buffer = new Uint8Array([1, 2, 3]).buffer;
        await downloadFile('chart.bin', buffer, 'application/octet-stream');

        assert.deepEqual([...await bytesOf(browser.record.blobs[0])], [1, 2, 3]);
    } finally {
        browser.restore();
    }
});

test('a Blob input is delivered unchanged', async () => {
    const browser = installBrowser();
    try {
        const svg = new Blob(['<svg></svg>'], { type: 'image/svg+xml' });
        await downloadFile('chart.svg', svg, 'text/plain');

        assert.equal(browser.record.blobs[0], svg);
    } finally {
        browser.restore();
    }
});

test('unsafe path characters are removed from the download name', async () => {
    const cases = [
        ['..\\..\\evil.csv', 'evil.csv'],
        ['a/b/c.csv', 'c.csv'],
        ['bad:name?.csv', 'bad_name_.csv'],
        ['a<b>c|d*.csv', 'a_b_c_d_.csv'],
        ['  report.csv  ', 'report.csv'],
        ['..', 'download'],
        ['', 'download'],
        [undefined, 'download'],
        ['\u0000report\u0001.csv', 'report.csv'],
    ];

    for (const [input, expected] of cases) {
        const browser = installBrowser();
        try {
            await downloadFile(input, 'x', 'text/plain');
            assert.equal(browser.anchor.download, expected, String(input));
        } finally {
            browser.restore();
        }
    }
});

test('Unicode file names are preserved', async () => {
    const browser = installBrowser();
    try {
        await downloadFile('温度 记录.csv', 'x', 'text/plain');
        assert.equal(browser.anchor.download, '温度 记录.csv');
    } finally {
        browser.restore();
    }
});

test('a failed click still removes the anchor and revokes the object URL', async () => {
    const browser = installBrowser({
        onClick() {
            throw new Error('download blocked');
        },
    });
    try {
        await assert.rejects(
            downloadFile('chart.csv', 'x', 'text/plain'),
            /download blocked/);

        assert.deepEqual(browser.record.appended, [browser.anchor]);
        assert.deepEqual(browser.record.removed, [browser.anchor]);
        assert.deepEqual(browser.record.revoked, [browser.record.urls[0]]);
        assert.equal(browser.anchor.parentNode, null);
    } finally {
        browser.restore();
    }
});

test('a failed append still revokes the object URL', async () => {
    const browser = installBrowser({
        onAppend() {
            throw new Error('no body');
        },
    });
    try {
        await assert.rejects(
            downloadFile('chart.csv', 'x', 'text/plain'),
            /no body/);

        assert.deepEqual(browser.record.revoked, [browser.record.urls[0]]);
        assert.equal(browser.anchor.parentNode, null);
    } finally {
        browser.restore();
    }
});

test('a failed anchor creation still revokes the object URL', async () => {
    const browser = installBrowser({
        onCreateElement() {
            throw new Error('no element');
        },
    });
    try {
        await assert.rejects(
            downloadFile('chart.csv', 'x', 'text/plain'),
            /no element/);

        assert.deepEqual(browser.record.revoked, [browser.record.urls[0]]);
    } finally {
        browser.restore();
    }
});

test('a failed anchor removal still revokes the object URL', async () => {
    const browser = installBrowser({
        onRemove() {
            throw new Error('remove failed');
        },
    });
    try {
        await assert.rejects(
            downloadFile('chart.csv', 'x', 'text/plain'),
            /remove failed/);

        assert.deepEqual(browser.record.revoked, [browser.record.urls[0]]);
    } finally {
        browser.restore();
    }
});

test('an unsupported content value rejects without creating an object URL', async () => {    const browser = installBrowser();
    try {
        await assert.rejects(
            downloadFile('chart.csv', { text: 'x' }, 'text/plain'),
            /content/i);

        assert.deepEqual(browser.record.blobs, []);
        assert.deepEqual(browser.record.revoked, []);
    } finally {
        browser.restore();
    }
});

test('a missing browser document rejects without side effects', async () => {
    const originalDocument = globalThis.document;
    const originalUrl = globalThis.URL;
    const calls = [];
    delete globalThis.document;
    globalThis.URL = {
        createObjectURL() {
            calls.push('create');
            return 'blob:test/1';
        },
        revokeObjectURL() {
            calls.push('revoke');
        },
    };

    try {
        await assert.rejects(
            downloadFile('chart.csv', 'x', 'text/plain'),
            /browser document/);
        assert.deepEqual(calls, []);
    } finally {
        globalThis.document = originalDocument;
        globalThis.URL = originalUrl;
    }
});
