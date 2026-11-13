import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';

const modulePath = new URL('../../src/MqttProbe.UI/wwwroot/js/chart-renderer.js', import.meta.url);
const source = await readFile(modulePath, 'utf8');
const renderer = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);
const { createChart, updateChart, disposeChart } = renderer;

function createFakeEcharts() {
    const charts = [];
    return {
        charts,
        init(element) {
            const chart = {
                element,
                disposed: false,
                options: [],
                resizes: 0,
                handlers: new Map(),
                setOption(option, options) {
                    this.options.push({ option, options });
                },
                on(name, handler) {
                    this.handlers.set(name, handler);
                },
                resize() {
                    this.resizes++;
                },
                dispose() {
                    this.disposed = true;
                },
                emit(name, payload) {
                    this.handlers.get(name)?.(payload);
                },
            };
            charts.push(chart);
            return chart;
        },
    };
}

function createElement() {
    return { isConnected: true, clientWidth: 640, clientHeight: 300 };
}

function createDotNetRef() {
    return {
        calls: [],
        invokeMethodAsync(name, ...args) {
            this.calls.push({ name, args });
            return Promise.resolve();
        },
    };
}

function installResizeObserver() {
    const observers = [];
    const previous = globalThis.ResizeObserver;
    globalThis.ResizeObserver = class {
        constructor(callback) {
            this.callback = callback;
            this.disconnected = false;
            this.observed = null;
            observers.push(this);
        }

        observe(element) {
            this.observed = element;
        }

        disconnect() {
            this.disconnected = true;
        }

        trigger() {
            this.callback();
        }
    };

    return {
        observers,
        restore() {
            if (previous === undefined) {
                delete globalThis.ResizeObserver;
            } else {
                globalThis.ResizeObserver = previous;
            }
        },
    };
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
            series('serie-a', 'Temp', '#F97316', [[1767225600000, 21.5], [1767225607000, 22.25]]),
            series('serie-b', 'Flow', '#A78BFA', [[1767225603000, 3]]),
        ],
        ...overrides,
    };
}

let nextId = 1;
function uniqueId() {
    return `chart-${nextId++}`;
}

test('create applies an explicit time-series option synchronously', async () => {
    const echarts = createFakeEcharts();
    const element = createElement();
    const dotNetRef = createDotNetRef();
    const layout = installResizeObserver();
    try {
        const applied = await createChart(element, uniqueId(), dotNetRef, spec(), { echarts });

        assert.equal(applied, true, 'create reports success to the caller');
        assert.equal(echarts.charts.length, 1);

        const chart = echarts.charts[0];
        assert.equal(chart.element, element);
        assert.equal(chart.options.length, 1, 'the first option is applied inside create');

        const { option, options } = chart.options[0];
        assert.equal(options.notMerge, true, 'a new instance starts from a clean option tree');
        assert.equal(options.lazyUpdate, false, 'data is applied without waiting for a frame or paint');
        assert.equal(option.animation, false);
        assert.equal(option.progressive, 0);
        assert.equal(option.backgroundColor, 'transparent');
        assert.equal(option.xAxis.type, 'time');
        assert.equal(option.yAxis.type, 'value');
        assert.equal(option.dataZoom[0].type, 'inside');
        assert.equal(option.dataZoom[0].filterMode, 'none');
        assert.equal(option.tooltip.trigger, 'axis');
        assert.equal(option.tooltip.renderMode, 'richText', 'rich text keeps series names out of HTML');
        assert.equal(option.legend.show, true);
        assert.equal(option.legend.textStyle.color, '#0F172A', 'legend labels use the chart foreground');
        assert.equal(option.xAxis.splitLine.show, true, 'the Apex baseline draws both axis grids');
        assert.deepEqual(option.xAxis.splitLine.lineStyle, { color: '#CBD5E1', type: [4, 4] });
        assert.equal(typeof option.grid.left, 'number', 'the layout is stated, not inherited from defaults');
        assert.equal(typeof option.textStyle.color, 'string');
        assert.equal(option.textStyle.fontFamily, 'JetBrains Mono, monospace');

        assert.deepEqual(
            option.series.map(entry => [entry.id, entry.name, entry.type, entry.itemStyle.color]),
            [
                ['serie-a', 'serie-a', 'line', '#F97316'],
                ['serie-b', 'serie-b', 'line', '#A78BFA'],
            ],
            'the ECharts series name is the stable series id so equal labels stay distinct entries');
        assert.equal(option.legend.formatter('serie-a'), 'Temp', 'the legend shows the display label');
        assert.equal(option.legend.formatter('serie-b'), 'Flow');
        assert.deepEqual(
            option.series[0].data,
            [[1767225600000, 21.5], [1767225607000, 22.25]],
            'every stored instant and value survives, including the seven second gap');
        assert.equal(option.series[0].encode.x, 0);
        assert.equal(option.series[0].encode.y, 1);
        assert.equal(option.series[0].showSymbol, false);
        assert.equal(option.series[0].smooth, true, 'the current chart draws smoothed lines');
    } finally {
        layout.restore();
    }
});

test('area and bar selections map to the matching ECharts series', async () => {
    const echarts = createFakeEcharts();
    const layout = installResizeObserver();
    try {
        await createChart(createElement(), uniqueId(), createDotNetRef(),
            spec({ type: 'area', series: [series('only', 'Temp', '#F97316', [[1000, 1]])] }), { echarts });
        await createChart(createElement(), uniqueId(), createDotNetRef(),
            spec({ type: 'bar', series: [series('only', 'Temp', '#F97316', [[1000, 1]])] }), { echarts });

        const area = echarts.charts[0].options[0].option;
        const bar = echarts.charts[1].options[0].option;

        assert.equal(area.series[0].type, 'line');
        assert.ok(area.series[0].areaStyle, 'an area chart fills under the line');
        assert.equal(area.legend.show, false, 'one series needs no legend');
        assert.equal(bar.series[0].type, 'bar');
        assert.equal(bar.series[0].areaStyle, null, 'a bar has no fill under it');
    } finally {
        layout.restore();
    }
});

test('duplicate display names keep separate legend entries and visible labels', async () => {
    const echarts = createFakeEcharts();
    const layout = installResizeObserver();
    try {
        await createChart(createElement(), uniqueId(), createDotNetRef(), spec({
            series: [
                series('serie-a', 'Temp', '#F97316', [[1000, 1]]),
                series('serie-b', 'Temp', '#A78BFA', [[1000, 2]]),
            ],
        }), { echarts });

        const option = echarts.charts[0].options[0].option;
        assert.deepEqual(option.series.map(entry => entry.name), ['serie-a', 'serie-b'],
            'equal labels must not collapse into one legend entry');
        assert.equal(option.legend.formatter('serie-a'), 'Temp');
        assert.equal(option.legend.formatter('serie-b'), 'Temp');

        const tooltip = option.tooltip.formatter([
            { seriesName: 'serie-a', value: [1000, 1], color: '#F97316' },
            { seriesName: 'serie-b', value: [1000, 2], color: '#A78BFA' },
        ]);
        assert.match(tooltip, /Temp: 1/);
        assert.match(tooltip, /Temp: 2/);
        assert.match(tooltip, /\{marker_F97316\|/u, 'each line carries its own series colour');
        assert.match(tooltip, /\{marker_A78BFA\|/u);
    } finally {
        layout.restore();
    }
});

test('renaming a series updates its visible label and leaves legend selection alone', async () => {
    const echarts = createFakeEcharts();
    const layout = installResizeObserver();
    try {
        const id = uniqueId();
        await createChart(createElement(), id, createDotNetRef(), spec(), { echarts });
        const chart = echarts.charts[0];

        updateChart(id, spec({
            series: [
                series('serie-a', 'Temperature', '#F97316', [[1767225600000, 21.5]]),
                series('serie-b', 'Flow', '#A78BFA', [[1767225603000, 3]]),
            ],
        }));

        const { option } = chart.options[1];
        assert.equal(option.legend.formatter('serie-a'), 'Temperature');
        assert.equal(option.legend.formatter('serie-b'), 'Flow');
        assert.equal(option.legend.selected, undefined, 'a rename must not reset a hidden series');
        assert.match(option.tooltip.formatter([
            { seriesName: 'serie-a', value: [1767225600000, 21.5], color: '#F97316' },
        ]), /Temperature: 21\.5/);
    } finally {
        layout.restore();
    }
});

test('axis labels follow the shared time bridge preferences', async () => {
    const echarts = createFakeEcharts();
    const layout = installResizeObserver();
    const seen = [];
    const previous = globalThis.mqttProbeTime;
    globalThis.mqttProbeTime = {
        formatAxis(epochMs, options) {
            seen.push({ epochMs, options });
            return epochMs % 2 === 0 ? '00:00:00.000' : '12:34:56.789';
        },
        formatTooltip(epochMs, options) {
            return `2026-01-01 12:34:56.789 UTC`;
        },
    };
    try {
        await createChart(createElement(), uniqueId(), createDotNetRef(),
            spec({ zone: 'local', clockFormat: '12h' }), { echarts });

        const option = echarts.charts[0].options[0].option;
        const label = option.xAxis.axisLabel.formatter(1767225600000);

        assert.equal(seen.length, 1);
        assert.deepEqual(seen[0].options, { zone: 'local', clockFormat: '12h' });
        assert.equal(label, '2026-01-01\n00:00:00.000', 'midnight carries the date line');

        const midday = option.xAxis.axisLabel.formatter(1767225600001);
        assert.equal(midday, '12:34:56.789');
    } finally {
        globalThis.mqttProbeTime = previous;
        layout.restore();
    }
});

test('update merges series without resending zoom or legend selection', async () => {
    const echarts = createFakeEcharts();
    const layout = installResizeObserver();
    try {
        const id = uniqueId();
        await createChart(createElement(), id, createDotNetRef(), spec(), { echarts });
        const chart = echarts.charts[0];

        const applied = updateChart(id, spec({
            series: [
                series('serie-a', 'Temp', '#F97316', [[1767225600000, 21.5], [1767225607000, 22.25], [1767225619000, 23]]),
                series('serie-b', 'Flow', '#A78BFA', [[1767225603000, 3]]),
            ],
        }));

        assert.equal(applied, true);
        assert.equal(chart.options.length, 2, 'update applies synchronously, without a frame callback');
        const { option, options } = chart.options[1];
        assert.equal(options.notMerge, undefined, 'an update never resets the option tree');
        assert.equal(options.lazyUpdate, false);
        assert.equal(options.replaceMerge, undefined, 'stable membership keeps the same series instances');
        assert.equal(option.dataZoom, undefined, 'zoom is user state and is not re-sent');
        assert.equal(option.legend, undefined, 'legend selection is user state and is not re-sent');
        assert.equal(option.series[0].data.length, 3);
    } finally {
        layout.restore();
    }
});

test('update replaces the series set when membership changes', async () => {
    const echarts = createFakeEcharts();
    const layout = installResizeObserver();
    try {
        const id = uniqueId();
        await createChart(createElement(), id, createDotNetRef(), spec(), { echarts });
        const chart = echarts.charts[0];

        updateChart(id, spec({ series: [series('serie-b', 'Flow', '#A78BFA', [[1767225603000, 4]])] }));

        const { option, options } = chart.options[1];
        assert.deepEqual(options.replaceMerge, ['series'], 'a removed series must not linger');
        assert.deepEqual(option.series.map(entry => entry.id), ['serie-b']);
        assert.equal(option.legend.show, false, 'the legend visibility follows the new series count');
        assert.equal(option.legend.selected, undefined);
    } finally {
        layout.restore();
    }
});

test('area to line clears the fill and restores the scaled axis', async () => {
    const echarts = createFakeEcharts();
    const layout = installResizeObserver();
    try {
        const id = uniqueId();
        await createChart(createElement(), id, createDotNetRef(),
            spec({ type: 'area', series: [series('a', 'Temp', '#F97316', [[1000, 1]])] }), { echarts });
        const chart = echarts.charts[0];

        updateChart(id, spec({ type: 'line', series: [series('a', 'Temp', '#F97316', [[1000, 1]])] }));

        const { option, options } = chart.options[1];
        assert.equal(option.series[0].areaStyle, null, 'a matching id must not keep the old area fill');
        assert.equal(option.yAxis.scale, true);
        assert.deepEqual(options.replaceMerge, ['series']);
        assert.equal(option.dataZoom, undefined, 'a type change leaves the zoom window alone');
        assert.equal(option.legend.selected, undefined, 'a type change leaves legend selection alone');
    } finally {
        layout.restore();
    }
});

test('line to bar asks for a zero baseline', async () => {
    const echarts = createFakeEcharts();
    const layout = installResizeObserver();
    try {
        const id = uniqueId();
        await createChart(createElement(), id, createDotNetRef(),
            spec({ series: [series('a', 'Temp', '#F97316', [[1000, 100]])] }), { echarts });
        const chart = echarts.charts[0];

        updateChart(id, spec({ type: 'bar', series: [series('a', 'Temp', '#F97316', [[1000, 100]])] }));

        const { option } = chart.options[1];
        assert.equal(option.series[0].type, 'bar');
        assert.equal(option.yAxis.scale, false, 'bars start at zero, so the axis must not stay scaled');
        assert.equal(option.series[0].areaStyle, null);
    } finally {
        layout.restore();
    }
});

test('bar to line and line to area update the type specific options', async () => {
    const echarts = createFakeEcharts();
    const layout = installResizeObserver();
    try {
        const id = uniqueId();
        await createChart(createElement(), id, createDotNetRef(),
            spec({ type: 'bar', series: [series('a', 'Temp', '#F97316', [[1000, 100]])] }), { echarts });
        const chart = echarts.charts[0];

        updateChart(id, spec({ series: [series('a', 'Temp', '#F97316', [[1000, 100]])] }));
        assert.equal(chart.options[1].option.yAxis.scale, true, 'a line chart may scale away from zero');
        assert.equal(chart.options[1].option.series[0].areaStyle, null);

        updateChart(id, spec({ type: 'area', series: [series('a', 'Temp', '#F97316', [[1000, 100]])] }));
        const area = chart.options[2].option.series[0];
        assert.ok(area.areaStyle, 'an area chart fills under the line');
        assert.equal(chart.options[2].option.yAxis.scale, true);
    } finally {
        layout.restore();
    }
});

test('dispose tears down the chart and observer, and later updates are ignored', async () => {
    const echarts = createFakeEcharts();
    const layout = installResizeObserver();
    try {
        const id = uniqueId();
        await createChart(createElement(), id, createDotNetRef(), spec(), { echarts });
        const chart = echarts.charts[0];
        assert.equal(layout.observers.length, 1);

        assert.equal(disposeChart(id), true);
        assert.equal(chart.disposed, true, 'the canvas and its handlers are released');
        assert.equal(layout.observers[0].disconnected, true, 'the resize observer stops observing');

        assert.equal(disposeChart(id), false, 'a second dispose is harmless');
        assert.equal(updateChart(id, spec()), false, 'a late update cannot revive a disposed chart');
        assert.equal(chart.options.length, 1);
    } finally {
        layout.restore();
    }
});

test('creating a chart twice on the same element disposes the earlier instance', async () => {
    const echarts = createFakeEcharts();
    const layout = installResizeObserver();
    try {
        const element = createElement();
        await createChart(element, uniqueId(), createDotNetRef(), spec(), { echarts });
        const id = uniqueId();
        await createChart(element, id, createDotNetRef(), spec({ series: [series('one', 'Temp', '#F97316', [[1000, 1]])] }), { echarts });

        assert.equal(echarts.charts.length, 2);
        assert.equal(echarts.charts[0].disposed, true, 'the replaced instance is released');
        assert.equal(echarts.charts[1].disposed, false);
        assert.equal(layout.observers[0].disconnected, true);
        assert.equal(updateChart(id, spec({ series: [series('one', 'Temp', '#F97316', [[2000, 2]])] })), true);
        assert.deepEqual(echarts.charts[1].options[1].option.series[0].data, [[2000, 2]]);
    } finally {
        layout.restore();
    }
});

test('viewport changes reach the host and a full zoom resets the viewport', async () => {
    const echarts = createFakeEcharts();
    const layout = installResizeObserver();
    try {
        const dotNetRef = createDotNetRef();
        const id = uniqueId();
        await createChart(createElement(), id, dotNetRef, spec(), { echarts });
        const chart = echarts.charts[0];

        chart.emit('dataZoom', { batch: [{ startValue: 1767225603000, endValue: 1767225607000 }] });
        assert.equal(dotNetRef.calls[0].name, 'OnEChartsViewportChanged');
        assert.deepEqual(dotNetRef.calls[0].args, [1767225603000, 1767225607000]);

        chart.emit('dataZoom', { start: 0, end: 100 });
        assert.equal(dotNetRef.calls[1].name, 'OnEChartsViewportChanged');
        assert.deepEqual(dotNetRef.calls[1].args, [null, null],
            'a reset returns to the full data range');
        assert.equal(dotNetRef.calls.length, 2);
    } finally {
        layout.restore();
    }
});

test('theme changes repaint both axes, the legend label and the tooltip', async () => {
    const echarts = createFakeEcharts();
    const layout = installResizeObserver();
    try {
        const id = uniqueId();
        await createChart(createElement(), id, createDotNetRef(), spec(), { echarts });
        const chart = echarts.charts[0];

        updateChart(id, spec({ dark: true }));

        const { option } = chart.options[1];
        assert.equal(option.textStyle.color, '#F1F5F9');
        assert.equal(option.xAxis.axisLabel.color, '#94A3B8');
        assert.equal(option.xAxis.splitLine.show, true);
        assert.deepEqual(option.xAxis.splitLine.lineStyle, { color: '#334155', type: [4, 4] });
        assert.equal(option.yAxis.splitLine.lineStyle.color, '#334155');
        assert.equal(option.legend.textStyle.color, '#F1F5F9');
        assert.equal(option.tooltip.backgroundColor, '#1E293B');
    } finally {
        layout.restore();
    }
});

test('the accessible font profile reaches every chart label', async () => {
    const echarts = createFakeEcharts();
    const layout = installResizeObserver();
    try {
        const id = uniqueId();
        await createChart(createElement(), id, createDotNetRef(),
            spec({ fontProfile: 'accessible' }), { echarts });
        const chart = echarts.charts[0];

        const created = chart.options[0].option;
        assert.equal(created.textStyle.fontFamily, 'OpenDyslexic Mono, monospace');
        assert.equal(created.legend.textStyle.fontFamily, 'OpenDyslexic Mono, monospace');
        assert.equal(created.tooltip.textStyle.fontFamily, 'OpenDyslexic Mono, monospace');

        updateChart(id, spec({ fontProfile: 'standard' }));
        assert.equal(chart.options[1].option.textStyle.fontFamily, 'JetBrains Mono, monospace',
            'a profile change repaints the chart with the host font');
        assert.equal(chart.options[1].option.legend.textStyle.fontFamily, 'JetBrains Mono, monospace');

        updateChart(id, spec({ fontProfile: 'standard' }));
        assert.equal(chart.options[2].option.textStyle, undefined,
            'an ordinary update does not resend chrome');
    } finally {
        layout.restore();
    }
});

test('a user zoom becomes an absolute window that ordinary updates keep', async () => {
    const echarts = createFakeEcharts();
    const layout = installResizeObserver();
    try {
        const dotNetRef = createDotNetRef();
        const id = uniqueId();
        await createChart(createElement(), id, dotNetRef, spec(), { echarts });
        const chart = echarts.charts[0];

        const from = 1767225603000;
        const to = 1767225607000;
        chart.emit('dataZoom', { batch: [{ startValue: from, endValue: to }] });

        assert.equal(chart.options.length, 2, 'the zoom is converted to an absolute window once');
        assert.deepEqual(chart.options[1].option.dataZoom, [{ startValue: from, endValue: to }]);
        assert.equal(chart.options[1].options.lazyUpdate, false);
        assert.deepEqual(dotNetRef.calls[0].args, [from, to]);

        updateChart(id, spec({
            series: [
                series('serie-a', 'Temp', '#F97316', [[1767225600000, 21.5], [1767225619000, 23]]),
                series('serie-b', 'Flow', '#A78BFA', [[1767225603000, 3]]),
            ],
        }));

        assert.equal(chart.options[2].option.dataZoom, undefined,
            'ordinary data updates leave the pinned window alone');

        chart.emit('dataZoom', { batch: [{ startValue: from, endValue: to }] });
        assert.equal(chart.options.length, 3, 'the same range must not loop or reapply');
        assert.equal(dotNetRef.calls.length, 1, 'the host is told once, not on every repeat');
    } finally {
        layout.restore();
    }
});

test('zooming out to the full range returns to following new data', async () => {
    const echarts = createFakeEcharts();
    const layout = installResizeObserver();
    try {
        const dotNetRef = createDotNetRef();
        const id = uniqueId();
        await createChart(createElement(), id, dotNetRef, spec(), { echarts });
        const chart = echarts.charts[0];

        chart.emit('dataZoom', { batch: [{ startValue: 1767225603000, endValue: 1767225607000 }] });
        chart.emit('dataZoom', { start: 0, end: 100 });

        assert.deepEqual(chart.options[2].option.dataZoom, [{ start: 0, end: 100 }],
            'a reset returns the window to the data extent');
        assert.deepEqual(dotNetRef.calls[1].args, [null, null]);

        updateChart(id, spec({
            series: [
                series('serie-a', 'Temp', '#F97316', [[1767225600000, 21.5], [1767225679000, 24]]),
                series('serie-b', 'Flow', '#A78BFA', [[1767225603000, 3]]),
            ],
        }));
        assert.equal(chart.options[3].option.dataZoom, undefined,
            'a reset window follows the growing data without being rewritten');

        chart.emit('dataZoom', { start: 0, end: 100 });
        assert.equal(chart.options.length, 4, 'a repeated full range applies nothing');
    } finally {
        layout.restore();
    }
});

test('a type change resets a pinned window and later updates stay unpinned', async () => {
    const echarts = createFakeEcharts();
    const layout = installResizeObserver();
    try {
        const id = uniqueId();
        await createChart(createElement(), id, createDotNetRef(), spec(), { echarts });
        const chart = echarts.charts[0];

        chart.emit('dataZoom', { batch: [{ startValue: 1767225603000, endValue: 1767225607000 }] });
        updateChart(id, spec({ type: 'bar' }));

        assert.deepEqual(chart.options[2].option.dataZoom, [{ start: 0, end: 100 }],
            'changing the chart type is a configuration change, not an incoming message');
        assert.equal(chart.options[2].option.legend?.selected, undefined,
            'the configuration change still leaves legend selection alone');

        updateChart(id, spec({
            type: 'bar',
            series: [series('serie-a', 'Temp', '#F97316', [[1767225600000, 21.5]])],
        }));
        assert.equal(chart.options[3].option.dataZoom, undefined,
            'nothing stays pinned after the reset');
    } finally {
        layout.restore();
    }
});

test('a missing ECharts module fails the create instead of rendering nothing', async () => {
    await assert.rejects(createChart(createElement(), uniqueId(), createDotNetRef(), spec()));
});

// The fake chart records what we ask for; these tests use the real bundle in SSR mode to prove
// the merge semantics ECharts actually applies when only the chart type changes.
const bundleUrl = new URL(
    '../../src/MqttProbe.UI/wwwroot/js/echarts/6.1.0/echarts.esm.min.mjs',
    import.meta.url);

async function createRealEcharts() {
    const real = await import(bundleUrl.href);
    const state = { instance: null };
    return {
        state,
        echarts: {
            init() {
                state.instance = real.init(null, null, { renderer: 'svg', ssr: true, width: 480, height: 240 });
                return state.instance;
            },
        },
    };
}

function realSpec(type) {
    return {
        type,
        dark: false,
        zone: 'utc',
        clockFormat: '24h',
        series: [
            series('a', 'Temp', '#F97316', [[1767225600000, 100], [1767225660000, 200]]),
            series('b', 'Flow', '#A78BFA', [[1767225600000, 50], [1767225660000, 150]]),
        ],
    };
}

test('real bundle: area to line clears the fill, resets the window and keeps legend state', async () => {
    const { echarts, state } = await createRealEcharts();
    const id = uniqueId();
    try {
        await createChart({ isConnected: true }, id, createDotNetRef(), realSpec('area'), { echarts });
        const chart = state.instance;

        chart.dispatchAction({ type: 'dataZoom', start: 20, end: 60 });
        chart.dispatchAction({ type: 'legendToggleSelect', name: 'b' });
        assert.equal(chart.renderToSVGString().includes('fill-opacity="0.25"'), true,
            'the area fixture is filled before the transition');

        updateChart(id, realSpec('line'));

        const option = chart.getOption();
        assert.equal(option.series[0].areaStyle ?? null, null, 'the merged series must not keep the old fill');
        assert.deepEqual(option.series.map(entry => entry.id), ['a', 'b'], 'series identity survives the transition');
        assert.equal(option.dataZoom[0].start, 0,
            'a chart type change is a configuration change, so the window resets to the data');
        assert.equal(option.dataZoom[0].end, 100);
        assert.equal(option.legend[0].selected.b, false, 'a hidden series stays hidden across the transition');
        assert.equal(chart.renderToSVGString().includes('fill-opacity="0.25"'), false,
            'nothing is filled after the change');
    } finally {
        disposeChart(id);
    }
});

test('real bundle: a pinned window keeps its absolute range as data grows and resets to follow', async () => {
    const { echarts, state } = await createRealEcharts();
    const id = uniqueId();
    try {
        await createChart({ isConnected: true }, id, createDotNetRef(), realSpec('line'), { echarts });
        const chart = state.instance;

        chart.dispatchAction({ type: 'dataZoom', start: 20, end: 60 });
        const pinned = chart.getOption().dataZoom[0];
        assert.equal(pinned.startValue, 1767225612000);
        assert.equal(pinned.endValue, 1767225636000);

        updateChart(id, {
            type: 'line',
            dark: false,
            zone: 'utc',
            clockFormat: '24h',
            series: [
                series('a', 'Temp', '#F97316', [[1767225600000, 100], [1767225660000, 200], [1767225720000, 300]]),
                series('b', 'Flow', '#A78BFA', [[1767225600000, 50], [1767225660000, 150], [1767225720000, 250]]),
            ],
        });
        assert.equal(chart.getOption().dataZoom[0].startValue, 1767225612000,
            'incoming data must not move the pinned window');
        assert.equal(chart.getOption().dataZoom[0].endValue, 1767225636000);

        chart.dispatchAction({ type: 'dataZoom', start: 0, end: 100 });
        updateChart(id, {
            type: 'line',
            dark: false,
            zone: 'utc',
            clockFormat: '24h',
            series: [
                series('a', 'Temp', '#F97316', [[1767225600000, 100], [1767225780000, 400]]),
                series('b', 'Flow', '#A78BFA', [[1767225600000, 50], [1767225780000, 350]]),
            ],
        });
        const following = chart.getOption().dataZoom[0];
        assert.equal(following.startValue, 1767225600000);
        assert.equal(following.endValue, 1767225780000,
            'after a reset the window follows the new extent');
    } finally {
        disposeChart(id);
    }
});

test('real bundle: duplicate labels toggle independently and a rename keeps the hidden state', async () => {
    const { echarts, state } = await createRealEcharts();
    const id = uniqueId();
    try {
        await createChart({ isConnected: true }, id, createDotNetRef(), spec({
            series: [
                series('serie-a', 'Temp', '#F97316', [[1767225600000, 1], [1767225660000, 2]]),
                series('serie-b', 'Temp', '#A78BFA', [[1767225600000, 3], [1767225660000, 4]]),
            ],
        }), { echarts });
        const chart = state.instance;

        assert.equal((chart.renderToSVGString().match(/>Temp</g) ?? []).length, 2,
            'both labels are drawn, not one collapsed entry');

        chart.dispatchAction({ type: 'legendToggleSelect', name: 'serie-a' });
        assert.deepEqual(chart.getOption().legend[0].selected,
            { 'serie-a': false, 'serie-b': true },
            'one hidden entry must not hide its duplicate');

        updateChart(id, spec({
            series: [
                series('serie-a', 'Temperature', '#F97316', [[1767225600000, 1], [1767225660000, 2]]),
                series('serie-b', 'Temp', '#A78BFA', [[1767225600000, 3], [1767225660000, 4]]),
            ],
        }));

        const option = chart.getOption();
        assert.equal(option.legend[0].selected['serie-a'], false,
            'renaming the series keeps it hidden');
        assert.equal(option.legend[0].formatter('serie-a'), 'Temperature');
        assert.equal(option.legend[0].formatter('serie-b'), 'Temp');
    } finally {
        disposeChart(id);
    }
});

test('real bundle: line to bar uses a zero baseline and reverses back', async () => {
    const { echarts, state } = await createRealEcharts();
    const id = uniqueId();
    try {
        await createChart({ isConnected: true }, id, createDotNetRef(), realSpec('line'), { echarts });
        const chart = state.instance;

        updateChart(id, realSpec('bar'));
        let option = chart.getOption();
        assert.equal(option.series[0].type, 'bar');
        assert.equal(option.yAxis[0].scale, false, 'a bar axis must not stay scaled away from zero');
        assert.equal(chart.renderToSVGString().includes('>0</text>'), true, 'the bar axis shows zero');

        updateChart(id, realSpec('line'));
        option = chart.getOption();
        assert.equal(option.series[0].type, 'line');
        assert.equal(option.yAxis[0].scale, true, 'the line axis scales again');
        assert.equal(chart.renderToSVGString().includes('>0</text>'), false, 'the line axis drops the zero baseline');
    } finally {
        disposeChart(id);
    }
});
