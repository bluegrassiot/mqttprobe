// Points arrive already windowed by ChartCard; this module only draws and never collects data.

const ECHARTS_MODULE_PATH = './echarts/6.1.0/echarts.esm.min.js';
const MILLISECONDS_PER_DAY = 86_400_000;
const MARKER_SIZE = 5;
// Mirrors the host --font-mono profile; canvas text cannot resolve a CSS variable.
const FONT_FAMILIES = {
    standard: 'JetBrains Mono, monospace',
    accessible: 'OpenDyslexic Mono, monospace',
};

const THEMES = {
    light: {
        foreground: '#0F172A',
        axisLabel: '#475569',
        axisLine: '#CBD5E1',
        tooltipBackground: '#FFFFFF',
    },
    dark: {
        foreground: '#F1F5F9',
        axisLabel: '#94A3B8',
        axisLine: '#334155',
        tooltipBackground: '#1E293B',
    },
};

const instances = new Map();
const elementCharts = new WeakMap();
let echartsModule;

async function loadEcharts() {
    if (!echartsModule) {
        echartsModule = await import(ECHARTS_MODULE_PATH);
    }

    return echartsModule;
}

function themeFor(spec) {
    return spec?.dark === true ? THEMES.dark : THEMES.light;
}

function fontFamilyFor(spec) {
    return spec?.fontProfile === 'accessible' ? FONT_FAMILIES.accessible : FONT_FAMILIES.standard;
}

function timePreferences(spec) {
    return {
        zone: spec?.zone === 'utc' ? 'utc' : 'local',
        clockFormat: spec?.clockFormat === '12h' ? '12h' : '24h',
    };
}

function dataExtent(seriesList) {
    let first = Infinity;
    let last = -Infinity;

    for (const entry of seriesList ?? []) {
        for (const point of entry?.points ?? []) {
            const timestamp = point?.[0];
            if (typeof timestamp !== 'number' || !Number.isFinite(timestamp)) {
                continue;
            }

            first = Math.min(first, timestamp);
            last = Math.max(last, timestamp);
        }
    }

    return first === Infinity ? null : [first, last];
}

function formatNumber(value) {
    if (typeof value !== 'number' || !Number.isFinite(value)) {
        return '';
    }

    return String(Math.round(value * 100) / 100);
}

// Mirrors the Apex axis formatter: a second date line at midnight or across more than a day.
function axisLabelFormatter(spec) {
    const extent = dataExtent(spec?.series);
    const span = extent ? extent[1] - extent[0] : 0;
    const preferences = timePreferences(spec);

    return value => {
        const when = Array.isArray(value) ? value[0] : value;
        const bridge = globalThis.mqttProbeTime;
        if (!bridge || typeof when !== 'number') {
            return '';
        }

        const time = bridge.formatAxis(when, preferences);
        const full = bridge.formatTooltip(when, preferences);
        const date = typeof full === 'string' ? full.slice(0, 10) : '';
        const midnight = time.startsWith('00:00:00') || time.startsWith('12:00:00.000 AM');
        return span > MILLISECONDS_PER_DAY || midnight ? `${date}\n${time}` : time;
    };
}

function axisPointerLabelFormatter(spec) {
    const preferences = timePreferences(spec);

    return params => {
        const value = params?.value;
        if (params?.axisDimension === 'x') {
            const when = Array.isArray(value) ? value[0] : value;
            const bridge = globalThis.mqttProbeTime;
            return bridge && typeof when === 'number'
                ? bridge.formatAxis(when, preferences)
                : formatNumber(when);
        }

        return formatNumber(value);
    };
}

// Legend and tooltip show display labels; the ECharts series name stays the stable series id.
function displayLabels(spec) {
    const labels = new Map();
    for (const entry of spec?.series ?? []) {
        labels.set(entry.id, entry.name);
    }

    return labels;
}

function sameDisplayLabels(current, next) {
    const before = displayLabels(current);
    const after = displayLabels(next);
    if (before.size !== after.size) {
        return false;
    }

    for (const [id, name] of before) {
        if (after.get(id) !== name) {
            return false;
        }
    }

    return true;
}

function legendFormatter(spec) {
    const labels = displayLabels(spec);
    return name => labels.get(name) ?? name;
}

function tooltipFormatter(spec) {
    const preferences = timePreferences(spec);
    const labels = displayLabels(spec);

    return params => {
        const list = Array.isArray(params) ? params : [params];
        if (list.length === 0 || !list[0]) {
            return '';
        }

        const first = list[0];
        const when = first.axisValue ?? (Array.isArray(first.value) ? first.value[0] : first.value);
        const bridge = globalThis.mqttProbeTime;
        const header = bridge && typeof when === 'number'
            ? bridge.formatTooltip(when, preferences)
            : formatNumber(when);
        const lines = [header];

        for (const item of list) {
            const value = Array.isArray(item.value) ? item.value[1] : item.value;
            const marker = typeof item.marker === 'string' ? item.marker : '';
            lines.push(`${marker}${labels.get(item.seriesName) ?? item.seriesName}: ${formatNumber(value)}`);
        }

        return lines.join('\n');
    };
}

function buildSeries(spec) {
    const isBar = spec?.type === 'bar';
    const isArea = spec?.type === 'area';

    return (spec?.series ?? []).map(entry => {
        const series = {
            id: entry.id,
            name: entry.id,
            type: isBar ? 'bar' : 'line',
            data: entry.points ?? [],
            encode: { x: 0, y: 1 },
            itemStyle: { color: entry.color },
            // ECharts merges a matching series id, so a previous area fill must be cleared by hand.
            areaStyle: isArea ? { color: entry.color, opacity: 0.25 } : null,
        };

        if (isBar) {
            series.barMaxWidth = 24;
            return series;
        }

        // A one point line has no segment, so its visible symbol is the only mark on the plot.
        series.symbol = 'circle';
        series.symbolSize = MARKER_SIZE;
        series.showSymbol = true;
        series.smooth = true;
        series.lineStyle = { width: 2, color: entry.color };

        return series;
    });
}

function buildAxes(spec, theme) {
    return {
        xAxis: {
            type: 'time',
            axisLine: { show: true, lineStyle: { color: theme.axisLine } },
            axisTick: { show: true, lineStyle: { color: theme.axisLine } },
            axisLabel: {
                color: theme.axisLabel,
                fontSize: 11,
                hideOverlap: true,
                formatter: axisLabelFormatter(spec),
            },
            splitLine: { show: true, lineStyle: { color: theme.axisLine, type: [4, 4] } },
        },
        yAxis: {
            type: 'value',
            scale: spec?.type !== 'bar',
            axisLine: { show: true, lineStyle: { color: theme.axisLine } },
            axisTick: { show: true, lineStyle: { color: theme.axisLine } },
            axisLabel: { color: theme.axisLabel, fontSize: 11, formatter: formatNumber },
            splitLine: { show: true, lineStyle: { color: theme.axisLine, type: [4, 4] } },
        },
    };
}

function buildTooltip(spec, theme) {
    return {
        trigger: 'axis',
        triggerOn: 'mousemove',
        renderMode: 'richText',
        confine: true,
        backgroundColor: theme.tooltipBackground,
        borderColor: theme.axisLine,
        borderWidth: 1,
        borderRadius: 6,
        padding: [8, 10],
        textStyle: { color: theme.foreground, fontFamily: fontFamilyFor(spec), fontSize: 12 },
        axisPointer: {
            type: 'cross',
            lineStyle: { color: theme.axisLine, type: 'dashed', width: 1 },
            crossStyle: { color: theme.axisLine, type: 'dashed', width: 1 },
            label: {
                backgroundColor: theme.tooltipBackground,
                borderColor: theme.axisLine,
                color: theme.foreground,
                formatter: axisPointerLabelFormatter(spec),
            },
        },
        formatter: tooltipFormatter(spec),
    };
}

function buildInitialOption(spec) {
    const theme = themeFor(spec);
    const showLegend = (spec?.series ?? []).length > 1;

    return {
        animation: false,
        progressive: 0,
        backgroundColor: 'transparent',
        textStyle: { color: theme.foreground, fontFamily: fontFamilyFor(spec), fontSize: 11 },
        color: (spec?.series ?? []).map(entry => entry.color),
        grid: {
            left: 8,
            right: 56,
            top: 8,
            bottom: showLegend ? 30 : 8,
            containLabel: true,
        },
        legend: {
            show: showLegend,
            type: 'scroll',
            bottom: 0,
            left: 'center',
            itemWidth: 12,
            itemHeight: 12,
            textStyle: { color: theme.foreground, fontFamily: fontFamilyFor(spec), fontSize: 11 },
            formatter: legendFormatter(spec),
        },
        tooltip: buildTooltip(spec, theme),
        ...buildAxes(spec, theme),
        dataZoom: [{ type: 'inside', xAxisIndex: 0, filterMode: 'none' }],
        series: buildSeries(spec),
    };
}

// Chrome-only option for theme and time-preference changes; series data is applied separately.
function buildChromeOption(spec) {
    const theme = themeFor(spec);
    const axes = buildAxes(spec, theme);

    return {
        textStyle: { color: theme.foreground, fontFamily: fontFamilyFor(spec) },
        legend: {
            textStyle: { color: theme.foreground, fontFamily: fontFamilyFor(spec) },
            formatter: legendFormatter(spec),
        },
        tooltip: buildTooltip(spec, theme),
        xAxis: axes.xAxis,
        yAxis: axes.yAxis,
    };
}

function sameSeriesIds(current, next) {
    const currentIds = (current?.series ?? []).map(entry => entry.id);
    const nextIds = (next?.series ?? []).map(entry => entry.id);
    return currentIds.length === nextIds.length
        && currentIds.every((id, index) => id === nextIds[index]);
}

function viewportRange(instance, payload) {
    const change = payload?.batch?.[0] ?? payload;
    if (!change) {
        return null;
    }

    const extent = dataExtent(instance.spec?.series);
    if (Number.isFinite(change.startValue) && Number.isFinite(change.endValue)) {
        const coversAll = extent
            && change.startValue <= extent[0]
            && change.endValue >= extent[1];
        return coversAll
            ? { from: null, to: null }
            : { from: change.startValue, to: change.endValue };
    }

    if (Number.isFinite(change.start) && Number.isFinite(change.end)) {
        if (change.start <= 0 && change.end >= 100) {
            return { from: null, to: null };
        }

        if (!extent) {
            return null;
        }

        const span = extent[1] - extent[0];
        return {
            from: extent[0] + (span * change.start / 100),
            to: extent[0] + (span * change.end / 100),
        };
    }

    return null;
}

function sameViewport(left, right) {
    return left?.from === right?.from && left?.to === right?.to;
}

function dispatchTip(instance, action) {
    if (typeof instance?.chart?.dispatchAction === 'function') {
        instance.chart.dispatchAction(action);
    }
}

function pointerInGrid(instance) {
    const pointer = instance.pointer;
    if (!pointer || typeof instance.chart.containPixel !== 'function') {
        return false;
    }

    try {
        return instance.chart.containPixel({ gridIndex: 0 }, [pointer.x, pointer.y]);
    } catch {
        return false;
    }
}

// ECharts caches the tooltip's raw data indices across setOption, so a live update must
// re-show at the same pointer to refresh the timestamp and values it is showing.
function refreshTooltip(instance) {
    if (!pointerInGrid(instance)) {
        return;
    }

    dispatchTip(instance, { type: 'showTip', x: instance.pointer.x, y: instance.pointer.y });

    // A stationary pointer receives no further mousemove events, and ECharts can schedule a
    // delayed hide after re-rendering under it. One recovery show keeps the same tooltip alive.
    clearTimeout(instance.tooltipRecovery);
    instance.tooltipRecovery = setTimeout(() => {
        instance.tooltipRecovery = null;
        if (!instance.disposed && pointerInGrid(instance)) {
            dispatchTip(instance, { type: 'showTip', x: instance.pointer.x, y: instance.pointer.y });
        }
    }, 150);
}

// A percent window drifts as the data extent grows, so a user range is stored as absolute values.
function applyViewport(instance, pinned) {
    instance.applyingViewport = true;
    try {
        instance.chart.setOption({
            dataZoom: [pinned
                ? { startValue: pinned.from, endValue: pinned.to }
                : { start: 0, end: 100 }],
        }, { lazyUpdate: false });
        refreshTooltip(instance);
    } finally {
        instance.applyingViewport = false;
    }
}

function notifyViewport(instance, dotNetRef, payload) {
    if (instance.applyingViewport) {
        return;
    }

    const range = viewportRange(instance, payload);
    if (!range) {
        return;
    }

    const pinned = range.from === null ? null : { from: range.from, to: range.to };
    if (sameViewport(instance.pinned, pinned)) {
        return;
    }

    instance.pinned = pinned;
    applyViewport(instance, pinned);

    if (typeof dotNetRef?.invokeMethodAsync !== 'function') {
        return;
    }

    Promise.resolve(dotNetRef.invokeMethodAsync('OnEChartsViewportChanged', range.from, range.to))
        .catch(() => {
            // The host circuit can close while a zoom is in flight; the chart is already disposable.
        });
}

function release(instance) {
    if (!instance || instance.disposed) {
        return false;
    }

    instance.disposed = true;
    instances.delete(instance.id);
    if (elementCharts.get(instance.element) === instance.id) {
        elementCharts.delete(instance.element);
    }

    if (instance.pointerListeners) {
        instance.element.removeEventListener('mousemove', instance.pointerListeners.onMove);
        instance.element.removeEventListener('mouseenter', instance.pointerListeners.onEnter);
        instance.element.removeEventListener('mouseleave', instance.pointerListeners.onLeave);
        instance.pointerListeners = null;
    }

    clearTimeout(instance.tooltipRecovery);
    instance.tooltipRecovery = null;
    instance.observer?.disconnect();
    instance.observer = null;
    instance.chart.dispose();
    return true;
}

export async function createChart(element, chartId, dotNetRef, spec, options = {}) {
    if (!element) {
        throw new Error('chart container is required');
    }

    const echarts = options.echarts ?? await loadEcharts();
    if (!echarts || typeof echarts.init !== 'function') {
        throw new Error('the ECharts module did not load before the chart was created');
    }

    if (element.isConnected === false) {
        throw new Error('the chart container is no longer attached');
    }

    const previousId = elementCharts.get(element);
    if (previousId !== undefined) {
        release(instances.get(previousId));
    }

    release(instances.get(chartId));

    const chart = echarts.init(element, null, { renderer: 'canvas' });
    const instance = {
        id: chartId,
        element,
        chart,
        spec,
        observer: null,
        disposed: false,
        pinned: null,
        applyingViewport: false,
        pointer: null,
        pointerListeners: null,
        tooltipRecovery: null,
    };

    try {
        chart.setOption(buildInitialOption(spec), { notMerge: true, lazyUpdate: false });
    } catch (error) {
        release(instance);
        throw error;
    }

    instances.set(chartId, instance);
    elementCharts.set(element, chartId);

    if (typeof ResizeObserver === 'function') {
        instance.observer = new ResizeObserver(() => {
            if (!instance.disposed) {
                instance.chart.resize();
            }
        });
        instance.observer.observe(element);
    }

    if (typeof element.addEventListener === 'function') {
        const trackPointer = event => {
            const rect = element.getBoundingClientRect();
            const x = event.clientX - rect.left;
            const y = event.clientY - rect.top;
            if (!Number.isFinite(x) || !Number.isFinite(y)) {
                return false;
            }

            instance.pointer = { x, y };
            return true;
        };
        const onMove = event => {
            trackPointer(event);
        };
        const onEnter = event => {
            if (trackPointer(event)) {
                refreshTooltip(instance);
            }
        };
        const onLeave = () => {
            instance.pointer = null;
            clearTimeout(instance.tooltipRecovery);
            instance.tooltipRecovery = null;
        };

        element.addEventListener('mousemove', onMove);
        element.addEventListener('mouseenter', onEnter);
        element.addEventListener('mouseleave', onLeave);
        instance.pointerListeners = { onMove, onEnter, onLeave };
    }

    chart.on('dataZoom', payload => notifyViewport(instance, dotNetRef, payload));
    return true;
}

export function updateChart(chartId, spec) {
    const instance = instances.get(chartId);
    if (!instance || instance.disposed) {
        return false;
    }

    const membershipChanged = !sameSeriesIds(instance.spec, spec);
    const typeChanged = instance.spec?.type !== spec?.type;
    const labelsChanged = !sameDisplayLabels(instance.spec, spec);
    const chromeChanged = instance.spec?.dark !== spec?.dark
        || instance.spec?.zone !== spec?.zone
        || instance.spec?.clockFormat !== spec?.clockFormat
        || instance.spec?.fontProfile !== spec?.fontProfile;
    const replaceSeries = membershipChanged || typeChanged;
    const tooltipChromeChanged = chromeChanged || typeChanged || labelsChanged || membershipChanged;

    // The axis scale follows the series type, so a type change refreshes the chrome as well.
    const option = tooltipChromeChanged ? buildChromeOption(spec) : {};
    option.series = buildSeries(spec);

    if (membershipChanged) {
        option.legend = { ...(option.legend ?? {}), show: (spec?.series ?? []).length > 1 };
    }

    // A configuration change can remove the data a pinned window was looking at; incoming
    // message updates never take this path.
    if (replaceSeries && instance.pinned) {
        instance.pinned = null;
        option.dataZoom = [{ start: 0, end: 100 }];
    }

    if (tooltipChromeChanged) {
        dispatchTip(instance, { type: 'hideTip' });
    }

    try {
        instance.chart.setOption(option, {
            lazyUpdate: false,
            ...(replaceSeries ? { replaceMerge: ['series'] } : {}),
        });
    } catch (error) {
        release(instance);
        throw error;
    }

    refreshTooltip(instance);
    instance.spec = spec;
    return true;
}

export function disposeChart(chartId) {
    return release(instances.get(chartId));
}
