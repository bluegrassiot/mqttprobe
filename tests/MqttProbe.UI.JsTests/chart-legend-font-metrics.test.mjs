import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';

const modulePath = new URL('../../src/MqttProbe.UI/wwwroot/js/chart-renderer.js', import.meta.url);
const bundleUrl = new URL(
    '../../src/MqttProbe.UI/wwwroot/js/echarts/6.1.0/echarts.esm.min.js',
    import.meta.url);
const source = await readFile(modulePath, 'utf8');
const real = await import(bundleUrl.href);

const measuredFonts = [];
let faceLoaded = false;
real.setPlatformAPI({
    measureText(text, font) {
        measuredFonts.push(font);
        const size = Number(/([\d.]+)px/.exec(font)?.[1] ?? 12);
        const ratio = font.includes('OpenDyslexic Mono') && faceLoaded ? 0.732 : 0.5498;
        return { width: size * ratio * text.length };
    },
});

let rendererGeneration = 0;
async function freshRenderer() {
    const token = ++rendererGeneration;
    return import(`data:text/javascript;base64,${Buffer.from(`${source}\n// ${token}`).toString('base64')}`);
}

function installFontSet(load) {
    const previous = globalThis.document;
    globalThis.document = { fonts: { load } };

    return {
        restore() {
            if (previous === undefined) {
                delete globalThis.document;
            } else {
                globalThis.document = previous;
            }
        },
    };
}

function createRealEcharts() {
    const state = {};
    return {
        state,
        echarts: {
            init() {
                state.instance = real.init(null, null, {
                    renderer: 'svg',
                    ssr: true,
                    width: 640,
                    height: 300,
                });
                return state.instance;
            },
        },
    };
}

function spec() {
    return {
        type: 'line',
        dark: true,
        zone: 'utc',
        clockFormat: '24h',
        fontProfile: 'accessible',
        series: [
            { id: 'a', name: 'Metric-1', color: '#F97316', points: [[1767225600000, 1], [1767225660000, 2]] },
            { id: 'b', name: 'Metric-2', color: '#A78BFA', points: [[1767225600000, 2], [1767225660000, 3]] },
        ],
    };
}

function legendNodes(chart) {
    return [...chart.renderToSVGString().matchAll(/<text([^>]*)>([^<]*)<\/text>/g)]
        .map(match => ({
            text: match[2],
            x: Number(/translate\(([-\d.]+) ([-\d.]+)\)/.exec(match[1])?.[1]),
        }))
        .filter(node => node.text === 'Metric-1' || node.text === 'Metric-2');
}

test('the loaded face spaces the legend items without overlap', async () => {
    const module = await freshRenderer();
    measuredFonts.length = 0;
    faceLoaded = false;
    const fontSet = installFontSet(() => Promise.resolve().then(() => {
        faceLoaded = true;
        return [{ family: 'OpenDyslexic Mono' }];
    }));
    const { state, echarts } = createRealEcharts();
    try {
        await module.createChart({ isConnected: true }, 'legend-loaded', {}, spec(), { echarts });
        const nodes = legendNodes(state.instance);

        assert.equal(nodes.length, 2);
        assert.ok(measuredFonts.some(font => font.includes('OpenDyslexic Mono')),
            'the selected face must be measured after it loads');

        const firstTextEnd = nodes[0].x + 17 + 11 * 0.732 * 'Metric-1'.length;
        assert.ok(nodes[1].x - firstTextEnd >= 8,
            `the second item must clear the first label: ${JSON.stringify(nodes)}`);
    } finally {
        fontSet.restore();
        module.disposeChart('legend-loaded');
    }
});

test('a failed load never measures the selected family', async () => {
    const module = await freshRenderer();
    measuredFonts.length = 0;
    faceLoaded = false;
    const fontSet = installFontSet(() => Promise.reject(new Error('missing face')));
    const { state, echarts } = createRealEcharts();
    try {
        await module.createChart({ isConnected: true }, 'legend-fallback', {}, spec(), { echarts });
        const nodes = legendNodes(state.instance);

        assert.equal(nodes.length, 2);
        assert.equal(measuredFonts.some(font => font.includes('OpenDyslexic Mono')), false,
            'fallback metrics must not be cached under the selected font key');

        const firstTextEnd = nodes[0].x + 17 + 11 * 0.5498 * 'Metric-1'.length;
        assert.ok(nodes[1].x - firstTextEnd >= 8,
            `the fallback legend must still keep the item gap: ${JSON.stringify(nodes)}`);
    } finally {
        fontSet.restore();
        module.disposeChart('legend-fallback');
    }
});
