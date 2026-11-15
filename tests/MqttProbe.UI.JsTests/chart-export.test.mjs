import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';

const rendererPath = new URL('../../src/MqttProbe.UI/wwwroot/js/chart-renderer.js', import.meta.url);
const exportPath = new URL('../../src/MqttProbe.UI/wwwroot/js/chart-export.js', import.meta.url);
const bundleUrl = new URL(
    '../../src/MqttProbe.UI/wwwroot/js/echarts/6.1.0/echarts.esm.min.js',
    import.meta.url);

const rendererSource = await readFile(rendererPath, 'utf8');
const exportSource = await readFile(exportPath, 'utf8');

function dataUrl(source) {
    return `data:text/javascript;base64,${Buffer.from(source).toString('base64')}`;
}

function rendererUrl() {
    return dataUrl(rendererSource);
}

function csvStubUrl() {
    return dataUrl(`
export function formatChartCsv(snapshot, selectedSeriesIds) {
    globalThis.__chartExportCsv.push({ snapshot, selectedSeriesIds: [...selectedSeriesIds] });
    return '\\uFEFFstub-csv';
}`);
}

function deliveryStubUrl() {
    return dataUrl(`
export function downloadFile(fileName, content, mimeType) {
    globalThis.__chartExportDeliveries.push({ fileName, content, mimeType });
    return Promise.resolve();
}`);
}

function fakeEchartsUrl() {
    return dataUrl(`
export function init(element, theme, options) {
    const state = globalThis.__chartExportEcharts;
    const instance = {
        element,
        applied: [],
        dataUrls: [],
        disposed: false,
        setOption(option) {
            this.applied.push(option);
        },
        getDataURL(request) {
            this.dataUrls.push(request);
            return 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==';
        },
        renderToSVGString() {
            return '<svg></svg>';
        },
        dispose() {
            this.disposed = true;
        },
    };
    state.instances.push(instance);
    state.initOptions.push(options);
    return instance;
}`);
}

function realEchartsUrl() {
    return dataUrl(`
import * as real from ${JSON.stringify(bundleUrl.href)};
export function init(container, theme, options) {
    const state = globalThis.__chartExportReal;
    state.initOptions.push(options);
    const chart = real.init(container, theme, options);
    const setOption = chart.setOption.bind(chart);
    chart.setOption = (option, settings) => {
        state.applied.push(option);
        return setOption(option, settings);
    };
    state.charts.push(chart);
    return chart;
}`);
}

async function loadExport(chartModuleUrl, echartsUrl = fakeEchartsUrl(), cacheToken = '') {
    const source = exportSource
        .replace('./chart-renderer.js?v=6', chartModuleUrl)
        .replace('./echarts/6.1.0/echarts.esm.min.js', echartsUrl)
        .replace('./chart-csv.js', csvStubUrl())
        .replace('./chart-file-delivery.js', deliveryStubUrl())
        .concat(`\n// ${cacheToken}`);
    return import(dataUrl(source));
}

function resetStubs() {
    globalThis.__chartExportCsv = [];
    globalThis.__chartExportDeliveries = [];
    globalThis.__chartExportEcharts = { instances: [], initOptions: [] };
    globalThis.__chartExportReal = { applied: [], charts: [], initOptions: [] };
}

function createElement() {
    return {
        isConnected: true,
        clientWidth: 640,
        clientHeight: 300,
        addEventListener() {},
        removeEventListener() {},
        getBoundingClientRect() {
            return { left: 0, top: 0, width: 640, height: 300 };
        },
    };
}

function createDotNetRef() {
    return { invokeMethodAsync: () => Promise.resolve() };
}

function createRendererEcharts() {
    const charts = [];
    return {
        charts,
        init(element) {
            const chart = {
                element,
                option: null,
                disposed: false,
                width: element.clientWidth,
                height: element.clientHeight,
                getWidth() {
                    return this.width;
                },
                getHeight() {
                    return this.height;
                },
                setOption(option) {
                    this.option = option;
                },
                getOption() {
                    return this.option;
                },
                on() {},
                containPixel() {
                    return false;
                },
                dispatchAction() {},
                resize() {},
                dispose() {
                    this.disposed = true;
                },
            };
            charts.push(chart);
            return chart;
        },
    };
}

async function withFakeDocument(run) {
    const created = [];
    const previous = globalThis.document;
    globalThis.document = {
        createElement(tagName) {
            const element = {
                tagName: tagName.toUpperCase(),
                parentNode: null,
                style: {},
                removed: false,
                remove() {
                    this.removed = true;
                },
            };
            created.push(element);
            return element;
        },
    };

    try {
        await run(created);
    } finally {
        if (previous === undefined) {
            delete globalThis.document;
        } else {
            globalThis.document = previous;
        }
    }

    return created;
}

function hexLuminance(hex) {
    const value = hex.replace('#', '');
    const channels = [0, 2, 4].map(offset => parseInt(value.slice(offset, offset + 2), 16) / 255);
    const linear = channels.map(channel =>
        channel <= 0.04045 ? channel / 12.92 : ((channel + 0.055) / 1.055) ** 2.4);
    return 0.2126 * linear[0] + 0.7152 * linear[1] + 0.0722 * linear[2];
}

function contrastRatio(foreground, background) {
    const first = hexLuminance(foreground);
    const second = hexLuminance(background);
    return (Math.max(first, second) + 0.05) / (Math.min(first, second) + 0.05);
}

function textFill(svg, text) {
    const match = new RegExp(`<text[^>]*fill="([^"]+)"[^>]*>${text}</text>`).exec(svg);
    return match?.[1] ?? null;
}

function firstAxisFill(svg) {
    const entries = [...svg.matchAll(/<text[^>]*fill="([^"]+)"[^>]*>([^<]*)<\/text>/g)];
    return entries.find(([, , content]) => /^(\d+|[\d:.]+ (AM|PM))$/.test(content))?.[1] ?? null;
}

function series(id, name, color, points) {
    return { id, name, color, points };
}

function spec(overrides = {}) {
    return {
        type: 'line',
        dark: false,
        zone: 'utc',
        clockFormat: '24h',
        series: [
            series('serie-a', 'Temp', '#F97316', [[1767225600000, 100], [1767225660000, 200]]),
            series('serie-b', 'Flow', '#A78BFA', [[1767225600000, 50], [1767225660000, 150]]),
        ],
        ...overrides,
    };
}

async function createChart(renderer, echarts, chartId, chartSpec = spec()) {
    await renderer.createChart(createElement(), chartId, createDotNetRef(), chartSpec, { echarts });
}

async function createLiveRealChart() {
    const real = await import(bundleUrl.href);
    const state = { instance: null };
    const echarts = {
        init(element) {
            const instance = real.init(null, null, {
                renderer: 'svg',
                ssr: true,
                width: element.clientWidth,
                height: element.clientHeight,
            });
            state.instance = instance;
            return {
                setOption: (option, settings) => instance.setOption(option, settings),
                getOption: () => instance.getOption(),
                getWidth: () => instance.getWidth(),
                getHeight: () => instance.getHeight(),
                on() {},
                containPixel() {
                    return false;
                },
                dispatchAction(action) {
                    instance.dispatchAction(action);
                },
                resize() {},
                dispose() {
                    instance.dispose();
                },
            };
        },
    };
    return { state, echarts };
}

test('the renderer exposes a synchronous export snapshot accessor', async () => {
    const renderer = await import(rendererUrl());

    assert.equal(typeof renderer.getChartExportSnapshot, 'function');
    assert.equal(renderer.getChartExportSnapshot('missing'), null,
        'an unavailable chart cannot produce a snapshot');
});

test('the export module reads the renderer through the versioned module URL', () => {
    assert.match(exportSource, /from '\.\/chart-renderer\.js\?v=6'/,
        'the export module must share the live renderer registry, not a second unversioned module');
    assert.equal(exportSource.match(/chart-renderer\.js/g)?.length, 1);
});

test('capture reports the applied zone, clock format and stable series metadata', async () => {
    resetStubs();
    const chartModuleUrl = rendererUrl();
    const renderer = await import(chartModuleUrl);
    const exporter = await loadExport(chartModuleUrl);
    const echarts = createRendererEcharts();
    await createChart(renderer, echarts, 'chart-1', spec({ zone: 'local', clockFormat: '12h' }));

    const snapshot = exporter.captureSnapshot('chart-1');

    assert.ok(snapshot.id);
    assert.equal(snapshot.zone, 'local');
    assert.equal(snapshot.clockFormat, '12h');
    assert.deepEqual(snapshot.series, [
        { id: 'serie-a', name: 'Temp', color: '#F97316', pointCount: 2 },
        { id: 'serie-b', name: 'Flow', color: '#A78BFA', pointCount: 2 },
    ], 'metadata comes from the captured spec, not the live chart after capture');
    assert.notEqual(exporter.captureSnapshot('chart-1').id, snapshot.id,
        'each capture gets its own release handle');
});

test('capture freezes points and names before later live updates', async () => {
    resetStubs();
    const chartModuleUrl = rendererUrl();
    const renderer = await import(chartModuleUrl);
    const exporter = await loadExport(chartModuleUrl);
    await createChart(renderer, createRendererEcharts(), 'chart-1');

    const snapshot = exporter.captureSnapshot('chart-1');

    renderer.updateChart('chart-1', spec({
        series: [
            series('serie-a', 'Temperature', '#F97316',
                [[1767225600000, 100], [1767225660000, 200], [1767225720000, 300]]),
            series('serie-b', 'Flow', '#A78BFA', [[1767225600000, 50]]),
        ],
    }));

    await exporter.exportSnapshot(snapshot.id, ['serie-a'], 'csv', 'chart.csv');

    const [call] = globalThis.__chartExportCsv;
    assert.equal(call.snapshot.series[0].name, 'Temp',
        'the captured display name survives a later rename');
    assert.deepEqual(call.snapshot.series[0].points,
        [[1767225600000, 100], [1767225660000, 200]],
        'the export never reads newer buffered points');
});

test('capture returns null for an unknown or disposed chart', async () => {
    resetStubs();
    const chartModuleUrl = rendererUrl();
    const renderer = await import(chartModuleUrl);
    const exporter = await loadExport(chartModuleUrl);
    await createChart(renderer, createRendererEcharts(), 'chart-1');

    assert.equal(exporter.captureSnapshot('chart-2'), null);
    renderer.disposeChart('chart-1');
    assert.equal(exporter.captureSnapshot('chart-1'), null);
});

test('capture is unavailable when the chart has no finite drawable size', async () => {
    resetStubs();
    const chartModuleUrl = rendererUrl();
    const renderer = await import(chartModuleUrl);
    const exporter = await loadExport(chartModuleUrl);
    const echarts = createRendererEcharts();
    await createChart(renderer, echarts, 'chart-1');
    const chart = echarts.charts[0];

    const sizes = [[0, 300], [640, 0], [Number.NaN, 300], [640, Number.POSITIVE_INFINITY]];
    for (const [width, height] of sizes) {
        chart.width = width;
        chart.height = height;
        assert.equal(exporter.captureSnapshot('chart-1'), null,
            `a ${width}x${height} chart must not invent a blank image`);
    }

    chart.width = 640;
    chart.height = 300;
    assert.ok(exporter.captureSnapshot('chart-1'), 'a drawable chart still captures');
});

test('release is idempotent and the snapshot is gone afterwards', async () => {
    resetStubs();
    const chartModuleUrl = rendererUrl();
    const renderer = await import(chartModuleUrl);
    const exporter = await loadExport(chartModuleUrl);
    await createChart(renderer, createRendererEcharts(), 'chart-1');
    const { id } = exporter.captureSnapshot('chart-1');

    exporter.releaseSnapshot(id);
    exporter.releaseSnapshot(id);

    await assert.rejects(
        exporter.exportSnapshot(id, ['serie-a'], 'csv', 'chart.csv'),
        /no longer available/);
    assert.equal(globalThis.__chartExportDeliveries.length, 0);
});

test('CSV export passes the captured format, zone and authoritative selection', async () => {
    resetStubs();
    const chartModuleUrl = rendererUrl();
    const renderer = await import(chartModuleUrl);
    const exporter = await loadExport(chartModuleUrl);
    await createChart(renderer, createRendererEcharts(), 'chart-1',
        spec({ zone: 'local', clockFormat: '12h' }));
    const { id } = exporter.captureSnapshot('chart-1');

    await exporter.exportSnapshot(id, ['serie-b'], 'csv', 'weather.csv');

    const [call] = globalThis.__chartExportCsv;
    assert.equal(call.snapshot.zone, 'local');
    assert.equal(call.snapshot.clockFormat, '12h');
    assert.equal(call.snapshot.timeZone, Intl.DateTimeFormat().resolvedOptions().timeZone,
        'the browser local IANA zone is resolved once at capture');
    assert.deepEqual(call.snapshot.series.map(entry => entry.id), ['serie-a', 'serie-b']);
    assert.deepEqual(call.selectedSeriesIds, ['serie-b']);

    const [delivery] = globalThis.__chartExportDeliveries;
    assert.equal(delivery.fileName, 'weather.csv');
    assert.equal(delivery.content, '\uFEFFstub-csv');
    assert.equal(delivery.mimeType, 'text/csv;charset=utf-8');
});

test('CSV export rejects an empty selection instead of writing a header only file', async () => {
    resetStubs();
    const chartModuleUrl = rendererUrl();
    const renderer = await import(chartModuleUrl);
    const exporter = await loadExport(chartModuleUrl);
    await createChart(renderer, createRendererEcharts(), 'chart-1');
    const { id } = exporter.captureSnapshot('chart-1');

    await assert.rejects(exporter.exportSnapshot(id, [], 'csv', 'chart.csv'), /at least one series/);
    assert.equal(globalThis.__chartExportDeliveries.length, 0);
});

test('every export format rejects an unknown selected series id', async () => {
    resetStubs();
    const chartModuleUrl = rendererUrl();
    const renderer = await import(chartModuleUrl);
    const exporter = await loadExport(chartModuleUrl);
    await createChart(renderer, createRendererEcharts(), 'chart-1');
    const { id } = exporter.captureSnapshot('chart-1');

    await withFakeDocument(async () => {
        for (const format of ['csv', 'png', 'svg']) {
            await assert.rejects(
                exporter.exportSnapshot(id, ['serie-a', 'missing'], format, `chart.${format}`),
                /unknown selected series: missing/,
                `${format} must not silently drop an unknown id`);
        }
    });

    assert.equal(globalThis.__chartExportCsv.length, 0);
    assert.equal(globalThis.__chartExportEcharts.instances.length, 0,
        'an invalid selection never renders an image');
    assert.equal(globalThis.__chartExportDeliveries.length, 0);
});

test('exports keep chart configuration order regardless of selection order', async () => {
    resetStubs();
    const chartModuleUrl = rendererUrl();
    const renderer = await import(chartModuleUrl);
    const exporter = await loadExport(chartModuleUrl);
    await createChart(renderer, createRendererEcharts(), 'chart-1');
    const { id } = exporter.captureSnapshot('chart-1');
    const reversed = ['serie-b', 'serie-a'];

    await exporter.exportSnapshot(id, reversed, 'csv', 'chart.csv');
    assert.deepEqual(globalThis.__chartExportCsv[0].selectedSeriesIds,
        ['serie-a', 'serie-b'], 'CSV series follow the captured configuration order');

    await withFakeDocument(async () => {
        await exporter.exportSnapshot(id, reversed, 'png', 'chart.png');
        await exporter.exportSnapshot(id, reversed, 'svg', 'chart.svg');
    });

    const [png, svg] = globalThis.__chartExportEcharts.instances;
    assert.deepEqual(png.applied[0].series.map(entry => entry.id), ['serie-a', 'serie-b']);
    assert.deepEqual(svg.applied[0].series.map(entry => entry.id), ['serie-a', 'serie-b']);
});

test('PNG export renders in a detached canvas instance and disposes it', async () => {
    resetStubs();
    const chartModuleUrl = rendererUrl();
    const renderer = await import(chartModuleUrl);
    const exporter = await loadExport(chartModuleUrl);
    const liveEcharts = createRendererEcharts();
    await createChart(renderer, liveEcharts, 'chart-1');
    const { id } = exporter.captureSnapshot('chart-1');

    const created = await withFakeDocument(() =>
        exporter.exportSnapshot(id, ['serie-a'], 'png', 'chart.png'));

    const state = globalThis.__chartExportEcharts;
    assert.equal(state.instances.length, 1);
    assert.deepEqual(state.initOptions[0],
        { renderer: 'canvas', width: 640, height: 300 },
        'the image uses the captured dimensions, not the current container layout');
    assert.equal(created.length, 1);
    assert.equal(state.instances[0].element, created[0]);
    assert.equal(created[0].parentNode, null, 'the export canvas is never attached to the page');

    const [option] = state.instances[0].applied;
    assert.deepEqual(option.series.map(entry => entry.id), ['serie-a']);
    assert.equal(option.animation, false);
    assert.deepEqual(option.tooltip, { show: false },
        'the tooltip is not part of an exported image');
    assert.equal(option.backgroundColor, '#F1F5F9',
        'the PNG paints the captured light surface instead of a transparent background');
    assert.deepEqual(option.legend[0].selected, { 'serie-a': true },
        'the explicit selection overrides live legend state');
    assert.deepEqual(state.instances[0].dataUrls[0].type, 'png');

    const [delivery] = globalThis.__chartExportDeliveries;
    assert.equal(delivery.fileName, 'chart.png');
    assert.equal(delivery.mimeType, 'image/png');
    assert.ok(delivery.content instanceof Blob);
    assert.equal(delivery.content.type, 'image/png');
    assert.ok(delivery.content.size > 0);
    assert.equal(state.instances[0].disposed, true, 'the temporary instance is disposed');
    assert.equal(created[0].removed, true, 'the detached element is cleaned up');
    assert.equal(liveEcharts.charts[0].option.series.length, 2,
        'the live chart is not mutated by the export');
});

test('SVG export renders real ECharts output with the captured zoom and hidden legend series', async () => {
    resetStubs();
    const chartModuleUrl = rendererUrl();
    const renderer = await import(chartModuleUrl);
    const exporter = await loadExport(chartModuleUrl, realEchartsUrl());
    const live = await createLiveRealChart();
    await createChart(renderer, live.echarts, 'chart-1');

    live.state.instance.dispatchAction({ type: 'dataZoom', start: 20, end: 60 });
    live.state.instance.dispatchAction({ type: 'legendToggleSelect', name: 'serie-b' });
    const zoomed = live.state.instance.getOption().dataZoom[0];
    const { id } = exporter.captureSnapshot('chart-1');
    globalThis.__chartExportReal.applied.length = 0;

    await exporter.exportSnapshot(id, ['serie-a', 'serie-b'], 'svg', 'chart.svg');

    const state = globalThis.__chartExportReal;
    assert.deepEqual(state.initOptions[0],
        { renderer: 'svg', ssr: true, width: 640, height: 300 },
        'the SVG instance is explicitly sized from the capture');
    const [option] = state.applied;
    assert.deepEqual(option.series.map(entry => entry.id), ['serie-a', 'serie-b'],
        'a series hidden in the live legend is still exported when selected');
    assert.equal(option.animation, false);
    assert.deepEqual(option.tooltip, { show: false },
        'the normalized tooltip array is replaced instead of spread into numeric keys');
    assert.equal(option.dataZoom[0].startValue, zoomed.startValue,
        'the captured zoom window is applied to the export instance');
    assert.equal(option.dataZoom[0].endValue, zoomed.endValue);

    const [delivery] = globalThis.__chartExportDeliveries;
    assert.equal(delivery.mimeType, 'image/svg+xml;charset=utf-8');
    assert.match(delivery.content, /^<svg/);
    assert.match(delivery.content, /width="640"/);
    assert.match(delivery.content, /#F97316/);
    assert.match(delivery.content, /#A78BFA/);
    assert.equal(textFill(delivery.content, 'Flow'), '#0F172A',
        'a series hidden in the live legend keeps active legend text in the export');

    const liveOption = live.state.instance.getOption();
    assert.deepEqual(liveOption.series.map(entry => entry.id), ['serie-a', 'serie-b'],
        'the live chart keeps its series after the export');
    assert.equal(liveOption.legend[0].selected['serie-b'], false,
        'the live legend selection is unchanged');
    assert.equal(state.charts[0].isDisposed(), true, 'the SSR instance is disposed');
    live.state.instance.dispose();
});

test('image exports paint the captured theme surface and keep legend and axis text readable', async () => {
    for (const dark of [false, true]) {
        resetStubs();
        const chartModuleUrl = rendererUrl();
        const renderer = await import(chartModuleUrl);
        const exporter = await loadExport(chartModuleUrl, realEchartsUrl(), `theme-${dark}`);
        const live = await createLiveRealChart();
        const chartId = `chart-${dark ? 'dark' : 'light'}`;
        await createChart(renderer, live.echarts, chartId, spec({ dark }));

        const { id } = exporter.captureSnapshot(chartId);
        await exporter.exportSnapshot(id, ['serie-a', 'serie-b'], 'svg', `${chartId}.svg`);

        const svg = globalThis.__chartExportDeliveries[0].content;
        const [applied] = globalThis.__chartExportReal.applied;
        const surface = dark ? '#1E293B' : '#F1F5F9';
        const foreground = dark ? '#F1F5F9' : '#0F172A';
        const axis = dark ? '#94A3B8' : '#475569';

        assert.equal(applied.backgroundColor, surface,
            `${dark ? 'dark' : 'light'} export uses the captured theme surface`);
        const background = svg.match(/<rect[^>]*fill="([^"]+)"/);
        assert.equal(background?.[1], surface,
            'the SVG paints a full-size background in the captured theme');
        assert.notEqual(background?.[1], 'none', 'the export background is not transparent');

        for (const name of ['Temp', 'Flow']) {
            const fill = textFill(svg, name);
            assert.equal(fill, foreground, `${name} keeps the captured legend text color`);
            assert.ok(contrastRatio(fill, surface) >= 4.5,
                `${name} legend contrast against the export surface is at least 4.5`);
        }

        const axisFill = firstAxisFill(svg);
        assert.equal(axisFill, axis, 'axis labels keep the captured theme color');
        assert.ok(contrastRatio(axisFill, surface) >= 4.5,
            'axis label contrast against the export surface is at least 4.5');
        assert.doesNotMatch(svg, /fill="#ccc(?:ccc)?"/i, 'no legend text falls back to inactive gray');
        live.state.instance.dispose();
    }
});

test('export rejects a missing snapshot and an unsupported format', async () => {
    resetStubs();
    const chartModuleUrl = rendererUrl();
    const renderer = await import(chartModuleUrl);
    const exporter = await loadExport(chartModuleUrl);
    await createChart(renderer, createRendererEcharts(), 'chart-1');
    const { id } = exporter.captureSnapshot('chart-1');

    await assert.rejects(exporter.exportSnapshot('missing', ['serie-a'], 'png', 'chart.png'),
        /no longer available/);
    await assert.rejects(exporter.exportSnapshot(id, ['serie-a'], 'pdf', 'chart.pdf'),
        /not supported/);
    assert.equal(globalThis.__chartExportDeliveries.length, 0);
});
