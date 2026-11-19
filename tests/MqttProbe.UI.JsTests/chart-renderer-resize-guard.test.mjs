import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';

const modulePath = new URL('../../src/MqttProbe.UI/wwwroot/js/chart-renderer.js', import.meta.url);
const source = await readFile(modulePath, 'utf8');
const renderer = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);
const { createChart, disposeChart } = renderer;

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
                actions: [],
                inGrid: true,
                setOption(option) {
                    this.options.push(option);
                },
                on() {
                },
                getZr() {
                    return { on() { } };
                },
                containPixel() {
                    return this.inGrid;
                },
                dispatchAction(action) {
                    this.actions.push(action);
                },
                resize() {
                    this.resizes++;
                },
                dispose() {
                    this.disposed = true;
                },
            };
            charts.push(chart);
            return chart;
        },
    };
}

function createElement(width = 640, height = 300) {
    const listeners = new Map();
    return {
        isConnected: true,
        clientWidth: width,
        clientHeight: height,
        addEventListener(name, handler) {
            listeners.set(name, handler);
        },
        removeEventListener(name, handler) {
            if (listeners.get(name) === handler) {
                listeners.delete(name);
            }
        },
        getBoundingClientRect() {
            return { left: 0, top: 0, width: this.clientWidth, height: this.clientHeight };
        },
        emit(name, event) {
            listeners.get(name)?.(event);
        },
        resizeTo(nextWidth, nextHeight) {
            this.clientWidth = nextWidth;
            this.clientHeight = nextHeight;
        },
    };
}

function createDotNetRef() {
    return {
        invokeMethodAsync() {
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
            observers.push(this);
        }

        observe() {
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

function spec() {
    return {
        type: 'line',
        dark: false,
        zone: 'utc',
        clockFormat: '24h',
        series: [
            { id: 'serie-a', name: 'Temp', color: '#F97316', points: [[1767225600000, 21.5]] },
        ],
    };
}

let nextId = 1;
function uniqueId() {
    return `resize-guard-${nextId++}`;
}

async function createObservedChart(echarts, element) {
    const id = uniqueId();
    await createChart(element, id, createDotNetRef(), spec(), { echarts });
    return { id, chart: echarts.charts[echarts.charts.length - 1] };
}

test('an observer callback while the container is hidden does not resize', async () => {
    const echarts = createFakeEcharts();
    const layout = installResizeObserver();
    try {
        const element = createElement();
        const { chart } = await createObservedChart(echarts, element);
        chart.element.emit('mousemove', { clientX: 100, clientY: 100 });
        chart.actions.length = 0;

        element.resizeTo(0, 0);
        layout.observers[0].trigger();

        assert.equal(chart.resizes, 0, 'a zero-size container cannot produce a useful resize');
        assert.deepEqual(chart.actions, [], 'a hidden chart must not touch its tooltip');
    } finally {
        layout.restore();
    }
});

test('an observer callback at the creation size does not resize', async () => {
    const echarts = createFakeEcharts();
    const layout = installResizeObserver();
    try {
        const { chart } = await createObservedChart(echarts, createElement());

        layout.observers[0].trigger();
        layout.observers[0].trigger();

        assert.equal(chart.resizes, 0, 'the observer initial callback repeats the size ECharts already has');
    } finally {
        layout.restore();
    }
});

test('restoring the same size after hiding does not resize', async () => {
    const echarts = createFakeEcharts();
    const layout = installResizeObserver();
    try {
        const element = createElement();
        const { chart } = await createObservedChart(echarts, element);

        element.resizeTo(0, 0);
        layout.observers[0].trigger();
        element.resizeTo(640, 300);
        layout.observers[0].trigger();

        assert.equal(chart.resizes, 0, 'a hidden round trip with no size change needs no resize');
    } finally {
        layout.restore();
    }
});

test('a genuine size change resizes once and refreshes the tooltip', async () => {
    const echarts = createFakeEcharts();
    const layout = installResizeObserver();
    try {
        const element = createElement();
        const { chart } = await createObservedChart(echarts, element);
        chart.element.emit('mousemove', { clientX: 120, clientY: 90 });
        chart.actions.length = 0;

        element.resizeTo(800, 400);
        layout.observers[0].trigger();

        assert.equal(chart.resizes, 1);
        assert.deepEqual(chart.actions, [{ type: 'showTip', x: 120, y: 90 }],
            'a real resize re-shows the tooltip at the tracked pointer');
    } finally {
        layout.restore();
    }
});

test('repeating the same positive size after a resize does not resize again', async () => {
    const echarts = createFakeEcharts();
    const layout = installResizeObserver();
    try {
        const element = createElement();
        const { chart } = await createObservedChart(echarts, element);

        element.resizeTo(800, 400);
        layout.observers[0].trigger();
        layout.observers[0].trigger();

        assert.equal(chart.resizes, 1);
    } finally {
        layout.restore();
    }
});

test('a viewport change that happens while hidden is applied once on return', async () => {
    const echarts = createFakeEcharts();
    const layout = installResizeObserver();
    try {
        const element = createElement();
        const { chart } = await createObservedChart(echarts, element);

        element.resizeTo(0, 0);
        layout.observers[0].trigger();
        element.resizeTo(700, 320);
        layout.observers[0].trigger();
        layout.observers[0].trigger();

        assert.equal(chart.resizes, 1, 'the first visible size is applied exactly once');
    } finally {
        layout.restore();
    }
});

test('a chart initialized hidden resizes on its first visible size', async () => {
    const echarts = createFakeEcharts();
    const layout = installResizeObserver();
    try {
        const element = createElement(0, 0);
        const { chart } = await createObservedChart(echarts, element);

        layout.observers[0].trigger();
        assert.equal(chart.resizes, 0);

        element.resizeTo(500, 300);
        layout.observers[0].trigger();

        assert.equal(chart.resizes, 1, 'the first non-zero size is a real layout change');
    } finally {
        layout.restore();
    }
});

test('a queued observer callback after disposal does not touch the chart', async () => {
    const echarts = createFakeEcharts();
    const layout = installResizeObserver();
    try {
        const element = createElement();
        const { id, chart } = await createObservedChart(echarts, element);
        disposeChart(id);

        element.resizeTo(900, 500);
        layout.observers[0].trigger();

        assert.equal(chart.resizes, 0, 'a disposed chart ignores a callback that was already queued');
        assert.deepEqual(chart.actions, []);
    } finally {
        layout.restore();
    }
});
