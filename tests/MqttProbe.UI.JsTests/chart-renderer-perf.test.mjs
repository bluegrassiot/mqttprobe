import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';

const modulePath = new URL('../../src/MqttProbe.UI/wwwroot/js/chart-renderer.js', import.meta.url);
const source = await readFile(modulePath, 'utf8');
let moduleSequence = 0;

function spec(overrides = {}) {
    return {
        type: 'line',
        dark: false,
        zone: 'utc',
        clockFormat: '24h',
        fontProfile: 'standard',
        series: [
            {
                id: 'series-a',
                name: 'Alpha',
                color: '#2563EB',
                points: [[1767225600000, 1.5], [1767225601000, 2.5]],
            },
        ],
        ...overrides,
    };
}

function createElement() {
    return {
        isConnected: true,
        addEventListener() {},
        removeEventListener() {},
        getBoundingClientRect() {
            return { left: 0, top: 0, width: 640, height: 300 };
        },
    };
}

function createEcharts(options = {}) {
    const charts = [];
    return {
        charts,
        init() {
            const handlers = new Map();
            const chart = {
                options: [],
                finish() {
                    for (const handler of [...(handlers.get('finished') ?? [])]) {
                        handler();
                    }
                },
                emit(name, payload) {
                    for (const handler of [...(handlers.get(name) ?? [])]) {
                        handler(payload);
                    }
                },
                setOption(option) {
                    this.options.push(option);
                },
                getOption() {
                    return null;
                },
                getModel() {
                    if (!options.model) {
                        throw new Error('the internal model is unavailable');
                    }
                    return options.model();
                },
                getWidth() {
                    return 640;
                },
                getHeight() {
                    return 300;
                },
                on(name, handler) {
                    const list = handlers.get(name) ?? [];
                    list.push(handler);
                    handlers.set(name, list);
                },
                off(name, handler) {
                    const list = handlers.get(name) ?? [];
                    handlers.set(name, list.filter(entry => entry !== handler));
                },
                containPixel() {
                    return options.containPixel === true;
                },
                convertToPixel(_finder, value) {
                    return options.convertToPixel ? options.convertToPixel(value) : null;
                },
                dispatchAction() {},
                resize() {},
                dispose() {},
            };
            charts.push(chart);
            return chart;
        },
    };
}

function createDriver() {
    let now = 0;
    let hidden = false;
    let nextHandle = 1;
    const timers = new Map();
    const frames = new Map();

    const runTimers = deadline => {
        let ran = true;
        while (ran) {
            ran = false;
            const due = [...timers.entries()]
                .filter(([, timer]) => timer.due <= deadline)
                .sort((left, right) => left[1].due - right[1].due);
            for (const [id, timer] of due) {
                if (!timers.has(id)) {
                    continue;
                }
                timers.delete(id);
                now = Math.max(now, timer.due);
                timer.callback();
                ran = true;
            }
        }
    };

    return {
        now: () => now,
        setTimeout(callback, delay) {
            const id = nextHandle++;
            timers.set(id, { callback, due: now + Math.max(0, delay) });
            return id;
        },
        clearTimeout(id) {
            timers.delete(id);
        },
        requestAnimationFrame(callback) {
            const id = nextHandle++;
            frames.set(id, callback);
            return id;
        },
        cancelAnimationFrame(id) {
            frames.delete(id);
        },
        hidden: () => hidden,
        setHidden(value) {
            hidden = value;
        },
        advance(ms) {
            const target = now + ms;
            runTimers(target);
            now = target;
        },
        moveTo(value) {
            now = Math.max(now, value);
        },
        runTimersLate(extraMs) {
            if (timers.size === 0) {
                return;
            }
            const due = Math.max(...[...timers.values()].map(timer => timer.due));
            now = due + extraMs;
            for (const [id, timer] of [...timers.entries()].sort((left, right) => left[1].due - right[1].due)) {
                if (!timers.has(id)) {
                    continue;
                }
                timers.delete(id);
                timer.callback();
            }
        },
        frame(timestamp) {
            const pending = [...frames.values()];
            frames.clear();
            for (const callback of pending) {
                callback(timestamp);
            }
        },
        framesPending: () => frames.size,
        timersPending: () => timers.size,
    };
}

async function loadSampler(driver) {
    globalThis.__mqttProbeChartPerfEnabled = true;
    if (driver) {
        globalThis.__mqttProbeChartPerfDriver = driver;
    } else {
        delete globalThis.__mqttProbeChartPerfDriver;
    }
    moduleSequence += 1;
    const module = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}#sampler-${moduleSequence}`);
    return { module, perf: globalThis.__mqttProbeChartPerf };
}

function records(perf) {
    const drained = perf.drain().trim();
    return drained ? drained.split('\n').map(line => JSON.parse(line)) : [];
}

function dotNetRef() {
    return { invokeMethodAsync: async () => {} };
}

test('an observed frame survives a later update and disposal', async () => {
    const driver = createDriver();
    const { module, perf } = await loadSampler(driver);
    const echarts = createEcharts();

    await module.createChart(createElement(), 'chart-keep', dotNetRef(), spec(), { echarts }, '1:1');
    driver.frame(16);

    driver.advance(100);
    module.updateChart('chart-keep', spec(), false, '1:2');
    module.disposeChart('chart-keep');

    const [create, update] = records(perf);
    assert.equal(create.rs, 'ok');
    assert.equal(create.r, 0, 'arrival latency is measured from the apply return');
    assert.equal(create.rt, 16000, 'the supplied rAF timestamp is supplemental');
    assert.equal(update.rs, 'unavailable');
    assert.equal(perf.snapshot().counts.frames, 1);
    assert.equal(perf.snapshot().counts.framesSuperseded, 0, 'an observed frame is never relabelled');
    assert.equal(perf.snapshot().counts.framesUnavailable, 1);
});

test('finished is cumulative from entry and the frame baseline is the apply return', async () => {
    const driver = createDriver();
    const { module, perf } = await loadSampler(driver);
    const echarts = createEcharts();

    await module.createChart(createElement(), 'chart-finished', dotNetRef(), spec(), { echarts }, '2:1');
    driver.frame(20);
    driver.advance(30);
    echarts.charts[0].finish();
    driver.advance(300);

    const [record] = records(perf);
    assert.equal(record.fs, 'ok');
    assert.equal(record.rs, 'ok');
    assert.ok(record.f >= 30000, `finished should be cumulative from entry, got ${record.f}`);
    assert.ok(record.f > record.r, 'finished follows the earlier frame opportunity');
    assert.equal(record.r, 0, 'arrival latency is measured from the apply return');
    assert.equal(record.rt, 20000, 'the supplied rAF timestamp is supplemental');
    assert.equal(record.a >= 0, true);
    assert.equal('rb' in record, false, 'the frame baseline is fixed and needs no label');
});

test('a missing finished event is reported separately from the observed frame', async () => {
    const driver = createDriver();
    const { module, perf } = await loadSampler(driver);
    const echarts = createEcharts();

    await module.createChart(createElement(), 'chart-no-finish', dotNetRef(), spec(), { echarts }, '3:1');
    driver.frame(18);
    driver.advance(300);

    const [record] = records(perf);
    assert.equal(record.fs, 'missing');
    assert.equal(record.f, null);
    assert.equal(record.rs, 'ok');
    assert.equal(perf.snapshot().counts.finishedMissing, 1);
    assert.equal(perf.snapshot().counts.frames, 1);
});

test('a finalizer that arrives after the absolute deadline is labelled late, not missing', async () => {
    const driver = createDriver();
    const { module, perf } = await loadSampler(driver);
    const echarts = createEcharts();

    await module.createChart(createElement(), 'chart-late', dotNetRef(), spec(), { echarts }, '4:1');
    driver.runTimersLate(100);

    const [record] = records(perf);
    assert.equal(record.rs, 'late');
    assert.equal(record.fs, 'late');
    assert.equal(perf.snapshot().counts.framesLate, 1);
    assert.equal(perf.snapshot().counts.framesMissing, 0);
    assert.equal(perf.snapshot().counts.finishedLate, 1);
});

test('a finished event after the deadline is not credited to the record', async () => {
    const driver = createDriver();
    const { module, perf } = await loadSampler(driver);
    const echarts = createEcharts();

    await module.createChart(createElement(), 'chart-late-finished', dotNetRef(), spec(), { echarts }, '11:1');
    driver.advance(300);
    echarts.charts[0].finish();
    driver.advance(300);

    const [record] = records(perf);
    assert.equal(record.fs, 'missing');
    assert.equal(record.f, null);
    assert.equal(perf.snapshot().counts.finished, 0);
    assert.equal(perf.snapshot().counts.finishedMissing, 1);
});

test('an update normalization failure keeps the chart alive and records a terminal row', async () => {
    const driver = createDriver();
    const { module, perf } = await loadSampler(driver);
    const echarts = createEcharts();

    await module.createChart(createElement(), 'chart-update-broken', dotNetRef(), spec(), { echarts }, '12:1');
    driver.frame(10);
    driver.advance(300);
    perf.reset();

    let reads = 0;
    const series = new Proxy({}, {
        get() {
            reads++;
            if (reads > 1) {
                throw new Error('normalization failed');
            }
            return 'series-a';
        },
    });

    assert.throws(() => module.updateChart('chart-update-broken', spec({ series: [series] }), false, '12:2'));
    assert.ok(module.getChartExportSnapshot('chart-update-broken'), 'a data failure must not dispose the chart');

    const [record] = records(perf);
    assert.equal(record.op, 'u');
    assert.equal(record.ap, 0);
    assert.equal(record.why, 'build');
    assert.equal(record.fs, 'error');
    assert.equal(perf.snapshot().counts.rejected, 1);
});

test('a supplied timestamp earlier than the base never makes arrival latency negative', async () => {
    const driver = createDriver();
    const { module, perf } = await loadSampler(driver);
    const echarts = createEcharts();

    await module.createChart(createElement(), 'chart-earlier', dotNetRef(), spec(), { echarts }, '5:1');
    driver.moveTo(10);
    driver.frame(-5);
    driver.advance(300);

    const [record] = records(perf);
    assert.equal(record.rs, 'ok');
    assert.equal(record.r, 10000, 'arrival latency comes from the monotonic clock');
    assert.equal(record.rt, -5000, 'the supplied timestamp is retained separately');
});

test('a bogus supplied timestamp does not hide a valid arrival', async () => {
    const driver = createDriver();
    const { module, perf } = await loadSampler(driver);
    const echarts = createEcharts();

    await module.createChart(createElement(), 'chart-bogus', dotNetRef(), spec(), { echarts }, '5:2');
    driver.moveTo(5);
    driver.frame(Number.NaN);
    driver.advance(300);

    const [record] = records(perf);
    assert.equal(record.rs, 'ok');
    assert.equal(record.r, 5000);
    assert.equal(record.rt, null);
});

test('a late frame callback is labelled late from its arrival time', async () => {
    const driver = createDriver();
    const { module, perf } = await loadSampler(driver);
    const echarts = createEcharts();

    await module.createChart(createElement(), 'chart-late-frame', dotNetRef(), spec(), { echarts }, '5:3');
    driver.moveTo(300);
    driver.frame(20);
    driver.advance(0);

    const [record] = records(perf);
    assert.equal(record.rs, 'late');
    assert.equal(record.r, 300000);
    assert.equal(record.rt, 20000);
    assert.equal(perf.snapshot().counts.framesLate, 1);
    assert.equal(perf.snapshot().counts.frames, 0);
});

test('a frame callback arriving after the document hides reports hidden, never a frame', async () => {
    const driver = createDriver();
    const { module, perf } = await loadSampler(driver);
    const echarts = createEcharts();

    await module.createChart(createElement(), 'chart-hidden-at-arrival', dotNetRef(), spec(), { echarts }, '5:4');
    driver.setHidden(true);
    driver.frame(10);
    driver.advance(300);

    const [record] = records(perf);
    assert.equal(record.rs, 'hidden');
    assert.equal(record.r, null);
    assert.equal(record.rt, 10000, 'the supplied timestamp is still recorded');
    assert.equal(perf.snapshot().counts.framesHidden, 1);
    assert.equal(perf.snapshot().counts.frames, 0);
});

test('becoming hidden before the deadline is reported as hidden, never as a frame', async () => {
    const driver = createDriver();
    const { module, perf } = await loadSampler(driver);
    const echarts = createEcharts();

    await module.createChart(createElement(), 'chart-hidden', dotNetRef(), spec(), { echarts }, '6:1');
    driver.setHidden(true);
    driver.advance(300);

    const [record] = records(perf);
    assert.equal(record.rs, 'hidden');
    assert.equal(record.r, null);
    assert.equal(perf.snapshot().counts.framesHidden, 1);
    assert.equal(perf.snapshot().counts.frames, 0);
});

test('an option build failure releases the initialized instance and records a terminal row', async () => {
    const driver = createDriver();
    const { module, perf } = await loadSampler(driver);
    const echarts = createEcharts();

    let reads = 0;
    const series = new Proxy({}, {
        get() {
            reads++;
            if (reads > 1) {
                throw new Error('option build failed');
            }
            return 'series-a';
        },
    });
    const broken = spec({ series: [series] });

    await assert.rejects(module.createChart(createElement(), 'chart-broken', dotNetRef(), broken, { echarts }, '7:1'));
    assert.equal(module.getChartExportSnapshot('chart-broken'), null, 'the initialized instance must not leak');
    assert.equal(module.disposeChart('chart-broken'), false);

    const [record] = records(perf);
    assert.equal(record.ap, 0);
    assert.equal(record.why, 'build');
    assert.equal(record.fs, 'error');
    assert.equal(record.rs, 'unavailable');
    assert.equal(perf.snapshot().counts.applyErrors, 0);
    assert.equal(perf.snapshot().counts.rejected, 1);
});

test('reset drops pending records without emitting them or keeping listeners', async () => {
    const driver = createDriver();
    const { module, perf } = await loadSampler(driver);
    const echarts = createEcharts();

    await module.createChart(createElement(), 'chart-reset', dotNetRef(), spec(), { echarts }, '8:1');
    driver.frame(12);
    perf.reset();
    driver.advance(300);
    echarts.charts[0].finish();
    driver.advance(300);

    assert.deepEqual(records(perf), []);
    assert.equal(perf.snapshot().counts.resetDropped, 1);
    assert.equal(perf.snapshot().pending, 0);
    assert.equal(perf.snapshot().counts.frames, 0);
    assert.equal(driver.timersPending(), 0);
});

test('an update for a missing instance keeps its exact token in a terminal row', async () => {
    const driver = createDriver();
    const { module, perf } = await loadSampler(driver);

    const applied = module.updateChart('chart-missing', spec(), false, '9:42');

    assert.equal(applied, false);
    const [record] = records(perf);
    assert.equal(record.op, 'u');
    assert.equal(record.g, 9);
    assert.equal(record.s, 42);
    assert.equal(record.ap, 0);
    assert.equal(record.why, 'nochart');
    assert.equal(record.rs, 'unavailable');
    assert.equal(perf.snapshot().counts.notApplied, 1);
});

test('the record buffer stays bounded and counts dropped lines', async () => {
    const driver = createDriver();
    const { module, perf } = await loadSampler(driver);
    const echarts = createEcharts();

    await module.createChart(createElement(), 'chart-overflow', dotNetRef(), spec(), { echarts }, '10:1');
    for (let index = 0; index < 4100; index++) {
        module.updateChart('chart-overflow', spec(), false, `10:${index + 2}`);
        driver.advance(300);
    }

    const snapshot = perf.snapshot();
    assert.equal(snapshot.bufferedLines, 4096);
    assert.ok(snapshot.counts.linesDropped > 0);
    assert.ok(snapshot.dropped > 0);
});

test('snapshot exposes compact per-instance state without names, values or point arrays', async () => {
    const driver = createDriver();
    const { module, perf } = await loadSampler(driver);
    const echarts = createEcharts({
        model: () => ({ getComponent: () => ({ axis: { scale: { getExtent: () => [1000, 3000] } } }) }),
        convertToPixel: () => [10, 20],
        containPixel: true,
    });
    const stateSpec = spec({
        series: [
            { id: 'series-a', name: 'Secret', color: '#2563EB', points: [[1000, 11.5], [2000, 12.5], [3000, 13.5]] },
            { id: 'series-b', name: 'Hidden', color: '#F97316', points: [[1500, 99.9]] },
        ],
    });

    await module.createChart(createElement(), 'chart-state', dotNetRef(), stateSpec, { echarts }, '7:3');
    const snapshot = perf.snapshot();
    const instance = snapshot.instances.find(entry => entry.c === 'chart-state');

    assert.ok(instance);
    assert.deepEqual(instance.entered, { g: 7, s: 3, op: 'c', at: instance.entered.at, atp: 0 });
    assert.deepEqual(instance.applied, { g: 7, s: 3, op: 'c', at: instance.applied.at, atp: 0 });
    assert.deepEqual(instance.series, [{ n: 3, t: 3000 }, { n: 1, t: 1500 }]);
    assert.deepEqual(instance.view, { from: 1000, to: 3000, source: 'scale' });
    assert.deepEqual(instance.inRange, [{ n: 3, t: 3000 }, { n: 1, t: 1500 }]);
    assert.deepEqual(instance.latest, { t: 3000, x: 10, y: 20, grid: true });
    assert.deepEqual(instance.canvas, { w: 640, h: 300 });
    assert.deepEqual(instance.box, { w: 640, h: 300 });
    assert.equal(instance.disposed, false);
    assert.equal(instance.connected, true);
    assert.equal(instance.visible, true);
    assert.equal(instance.pinned, null);
    assert.equal('spec' in instance, false);

    const text = JSON.stringify(instance);
    assert.equal(text.includes('Secret'), false);
    assert.equal(text.includes('Hidden'), false);
    assert.equal(text.includes('11.5'), false);
    assert.equal(text.includes('99.9'), false);
    assert.equal(text.includes('"points"'), false);
});

test('snapshot distinguishes a pinned viewport and an unsupported scale', async () => {
    const driver = createDriver();
    const { module, perf } = await loadSampler(driver);
    const echarts = createEcharts();

    await module.createChart(createElement(), 'chart-pinned', dotNetRef(), spec(), { echarts }, '8:1');
    const chart = echarts.charts[0];
    chart.emit('dataZoom', { startValue: 1200, endValue: 1800 });

    let instance = perf.snapshot().instances.find(entry => entry.c === 'chart-pinned');
    assert.deepEqual(instance.view, { from: 1200, to: 1800, source: 'pinned' });
    assert.deepEqual(instance.inRange, [{ n: 0, t: null }]);
    assert.equal(instance.latest.x, null);
    assert.equal(instance.latest.grid, null);

    module.updateChart('chart-pinned', spec(), true, '8:2');
    chart.emit('dataZoom', { start: 0, end: 100 });
    instance = perf.snapshot().instances.find(entry => entry.c === 'chart-pinned');
    assert.equal(instance.view, null, 'an unsupported scale must not invent a viewport');
    assert.equal(instance.inRange, null);
});

test('a real tooltip records point timestamps rather than the axis value', async () => {
    const driver = createDriver();
    const { module, perf } = await loadSampler(driver);
    const echarts = createEcharts();
    const stateSpec = spec({
        series: [
            { id: 'series-a', name: 'Alpha', color: '#2563EB', points: [[1000, 1.5], [2000, 2.5]] },
            { id: 'series-b', name: 'Beta', color: '#F97316', points: [[1500, 3.5], [2500, 4.5]] },
        ],
    });

    await module.createChart(createElement(), 'chart-tooltip', dotNetRef(), stateSpec, { echarts }, '9:1');
    const option = echarts.charts[0].options[0];
    option.tooltip.formatter([
        { axisValue: 1500, value: [2000, 2.5], seriesName: 'series-a' },
        { axisValue: 1500, value: [2500, 4.5], seriesName: 'series-b' },
    ]);

    const instance = perf.snapshot().instances.find(entry => entry.c === 'chart-tooltip');
    assert.deepEqual(instance.tooltip.t, [2000, 2500]);
    assert.ok(instance.tooltip.at > 0);
    assert.equal(JSON.stringify(instance.tooltip).includes('2.5'), false);
    assert.equal(JSON.stringify(instance.tooltip).includes('4.5'), false);
});

test('disposal removes the instance from the current-state snapshot', async () => {
    const driver = createDriver();
    const { module, perf } = await loadSampler(driver);
    const echarts = createEcharts();

    await module.createChart(createElement(), 'chart-dispose', dotNetRef(), spec(), { echarts }, '10:1');
    assert.equal(perf.snapshot().instances.some(entry => entry.c === 'chart-dispose'), true);

    module.disposeChart('chart-dispose');
    assert.equal(perf.snapshot().instances.some(entry => entry.c === 'chart-dispose'), false);
});

test('a superseded attempt reports ambiguous finished attribution', async () => {
    const driver = createDriver();
    const { module, perf } = await loadSampler(driver);
    const echarts = createEcharts();

    await module.createChart(createElement(), 'chart-ambiguous', dotNetRef(), spec(), { echarts }, '20:1');
    driver.advance(100);
    module.updateChart('chart-ambiguous', spec(), false, '20:2');
    driver.advance(300);

    const [create] = records(perf);
    assert.equal(create.fs, 'ambiguous');
    assert.equal(perf.snapshot().counts.finishedAmbiguous, 1);
});

test.after(() => {
    delete globalThis.__mqttProbeChartPerfEnabled;
    delete globalThis.__mqttProbeChartPerfDriver;
});
