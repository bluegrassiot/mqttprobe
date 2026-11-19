// Points arrive already windowed by ChartCard; this module only draws and never collects data.

const ECHARTS_MODULE_PATH = './echarts/6.1.0/echarts.esm.min.js';
const MILLISECONDS_PER_DAY = 86_400_000;
const MARKER_SIZE = 5;
// The axis labels need their own band; a visible legend row sits below it.
const AXIS_LABEL_BAND = 8;
const LEGEND_BAND = 30;
const CLOCK_TIME_PATTERN = /^(\d{2}:\d{2}):(\d{2})\.(\d{3})( .*)?$/;
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

// ECharts reports each tick's granularity as the formatter's third argument.
function axisTimeText(time, unit) {
    const match = CLOCK_TIME_PATTERN.exec(time);
    if (!match || !unit) {
        return time;
    }

    const [, hourMinute, second, fraction, suffix = ''] = match;
    if (unit === 'millisecond' || fraction !== '000') {
        return time;
    }

    if (unit === 'second' || second !== '00') {
        return `${hourMinute}:${second}${suffix}`;
    }

    return `${hourMinute}${suffix}`;
}

// Mirrors the Apex axis formatter: a second date line at midnight or across more than a day.
function axisLabelFormatter(spec) {
    const extent = dataExtent(spec?.series);
    const span = extent ? extent[1] - extent[0] : 0;
    const preferences = timePreferences(spec);

    return (value, _index, extra) => {
        const when = Array.isArray(value) ? value[0] : value;
        const bridge = globalThis.mqttProbeTime;
        if (!bridge || typeof when !== 'number') {
            return '';
        }

        const clock = bridge.formatAxis(when, preferences);
        const time = axisTimeText(clock, extra?.time?.upperTimeUnit);
        const full = bridge.formatTooltip(when, preferences);
        const date = typeof full === 'string' ? full.slice(0, 10) : '';
        const midnight = clock.startsWith('00:00:00') || clock.startsWith('12:00:00.000 AM');
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

function showsLegend(spec) {
    return (spec?.series ?? []).length > 1;
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

function tooltipFormatter(spec, perfState) {
    const preferences = timePreferences(spec);
    const labels = displayLabels(spec);

    return params => {
        const list = Array.isArray(params) ? params : [params];
        if (list.length === 0 || !list[0]) {
            return '';
        }

        recordPerfTooltip(perfState, list);
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

        series.symbol = 'circle';
        series.symbolSize = MARKER_SIZE;
        series.showSymbol = false;
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

function buildTooltip(spec, theme, perfState) {
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
        formatter: tooltipFormatter(spec, perfState),
    };
}

function buildInitialOption(spec, perfState) {
    const theme = themeFor(spec);
    const showLegend = showsLegend(spec);

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
            bottom: showLegend ? LEGEND_BAND : AXIS_LABEL_BAND,
            containLabel: false,
            outerBoundsMode: 'same',
            outerBoundsContain: 'axisLabel',
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
        tooltip: buildTooltip(spec, theme, perfState),
        ...buildAxes(spec, theme),
        dataZoom: [{ type: 'inside', xAxisIndex: 0, filterMode: 'none' }],
        series: buildSeries(spec),
    };
}

// Chrome-only option for theme and time-preference changes; series data is applied separately.
function buildChromeOption(spec, perfState) {
    const theme = themeFor(spec);
    const axes = buildAxes(spec, theme);

    return {
        textStyle: { color: theme.foreground, fontFamily: fontFamilyFor(spec) },
        legend: {
            textStyle: { color: theme.foreground, fontFamily: fontFamilyFor(spec) },
            formatter: legendFormatter(spec),
        },
        tooltip: buildTooltip(spec, theme, perfState),
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
        // A relayout or resize can move the grid under a stationary pointer; stale content must not remain.
        clearTimeout(instance.tooltipRecovery);
        instance.tooltipRecovery = null;
        if (instance.pointer) {
            dispatchTip(instance, { type: 'hideTip' });
        }
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
    closePending(instance, 'unavailable');
    instance.observer?.disconnect();
    instance.observer = null;
    instance.chart.dispose();
    return true;
}

// Temporary #209 sampler. A harness enables it by preinjecting
// globalThis.__mqttProbeChartPerfEnabled = true before the app starts; records are drained
// through globalThis.__mqttProbeChartPerf.drain(maxChars), at most 32 KiB per call.
const PERF_ENABLED_KEY = '__mqttProbeChartPerfEnabled';
const PERF_API_KEY = '__mqttProbeChartPerf';
const PERF_DRIVER_KEY = '__mqttProbeChartPerfDriver';
const PERF_VERSION = 1;
const PERF_MAX_LINES = 4096;
const PERF_MAX_DRAIN_CHARS = 32 * 1024;
const PERF_DEADLINE_MS = 250;
const PERF_FINALIZER_SLACK_MS = 25;

let perfBuffer = [];
let perfChars = 0;
let perfDropped = 0;
let perfCounts = null;
let perfOriginUtc = 0;
let perfEpoch = 0;
let perfNow = () => performance.now();
let perfSchedule = (callback, delay) => setTimeout(callback, delay);
let perfCancel = handle => clearTimeout(handle);
let perfRequestFrame = callback => (typeof globalThis.requestAnimationFrame === 'function'
    ? globalThis.requestAnimationFrame(callback)
    : 0);
let perfCancelFrame = handle => {
    if (typeof globalThis.cancelAnimationFrame === 'function') {
        globalThis.cancelAnimationFrame(handle);
    }
};
let perfHidden = () => globalThis.document?.hidden === true;

function perfEnabled() {
    return globalThis[PERF_ENABLED_KEY] === true;
}

function ensurePerf() {
    if (perfCounts !== null) {
        return;
    }

    const driver = globalThis[PERF_DRIVER_KEY];
    if (driver) {
        if (typeof driver.now === 'function') {
            perfNow = () => driver.now();
        }
        if (typeof driver.setTimeout === 'function') {
            perfSchedule = (callback, delay) => driver.setTimeout(callback, delay);
        }
        if (typeof driver.clearTimeout === 'function') {
            perfCancel = handle => driver.clearTimeout(handle);
        }
        if (typeof driver.requestAnimationFrame === 'function') {
            perfRequestFrame = callback => driver.requestAnimationFrame(callback);
        }
        if (typeof driver.cancelAnimationFrame === 'function') {
            perfCancelFrame = handle => driver.cancelAnimationFrame(handle);
        }
        if (typeof driver.hidden === 'function') {
            perfHidden = () => driver.hidden();
        }
    }

    perfOriginUtc = Math.round(Date.now() - performance.now());
    perfCounts = {
        creates: 0,
        updates: 0,
        rejected: 0,
        notApplied: 0,
        applyErrors: 0,
        finished: 0,
        finishedLate: 0,
        finishedMissing: 0,
        finishedAmbiguous: 0,
        frames: 0,
        framesLate: 0,
        framesMissing: 0,
        framesHidden: 0,
        framesSuperseded: 0,
        framesUnavailable: 0,
        resetDropped: 0,
        linesDropped: 0,
    };
    globalThis[PERF_API_KEY] = {
        version: PERF_VERSION,
        enabled: true,
        drain: drainPerf,
        snapshot: snapshotPerf,
        reset: resetPerf,
    };
}

const PERF_TOOLTIP_MAX = 32;

function newPerfState(chartId) {
    return { c: chartId, entered: null, applied: null, series: [], tooltip: null };
}

function perfToken(token) {
    if (typeof token !== 'string') {
        return null;
    }

    const parts = token.split(':');
    const g = Number(parts[0]);
    const s = Number(parts[1]);
    return Number.isFinite(g) && Number.isFinite(s) ? { g, s } : null;
}

function recordPerfEntered(state, token, operation, at, atPerf) {
    if (!state) {
        return;
    }

    const parsed = perfToken(token);
    if (parsed) {
        state.entered = { ...parsed, op: operation, at, atp: atPerf };
    }
}

function recordPerfApplied(instance, spec, at, atPerf) {
    const state = instance?.perfState;
    if (!state) {
        return;
    }

    const entered = state.entered;
    state.applied = entered
        ? { ...entered, at, atp: atPerf }
        : { g: -1, s: -1, op: '?', at, atp: atPerf };
    state.series = (spec?.series ?? []).map(entry => {
        const points = entry?.points ?? [];
        const last = points.length > 0 ? points[points.length - 1] : null;
        return { n: points.length, t: Number.isFinite(last?.[0]) ? last[0] : null };
    });
}

function recordPerfTooltip(state, list) {
    if (!state) {
        return;
    }

    const stamps = [];
    for (const item of list) {
        const value = Array.isArray(item?.value) ? item.value[0] : null;
        if (Number.isFinite(value)) {
            stamps.push(value);
        }
    }

    if (stamps.length > 0) {
        state.tooltip = {
            t: stamps.slice(0, PERF_TOOLTIP_MAX),
            at: Date.now(),
            atp: perfNow(),
        };
    }
}

function perfVisible() {
    return !perfHidden();
}

function roundOrNull(value) {
    return Number.isFinite(value) ? Math.round(value * 100) / 100 : null;
}

function tokenState(token) {
    return token
        ? { g: token.g, s: token.s, op: token.op, at: token.at, atp: token.atp }
        : null;
}

function dimension(read) {
    try {
        const value = read();
        return Number.isFinite(value) ? Math.round(value) : null;
    } catch {
        return null;
    }
}

function perfViewport(instance) {
    try {
        const model = instance.chart.getModel?.();
        const axis = model?.getComponent?.('xAxis', 0);
        const extent = axis?.axis?.scale?.getExtent?.();
        if (Array.isArray(extent) && Number.isFinite(extent[0]) && Number.isFinite(extent[1])) {
            return { from: Math.min(extent[0], extent[1]), to: Math.max(extent[0], extent[1]), source: 'scale' };
        }
    } catch {
        // Older ECharts builds do not expose the internal model; the pinned range stays available.
    }

    return instance.pinned
        ? { from: instance.pinned.from, to: instance.pinned.to, source: 'pinned' }
        : null;
}

function perfInRange(spec, view) {
    return (spec?.series ?? []).map(entry => {
        let count = 0;
        let latest = null;
        for (const point of entry?.points ?? []) {
            const timestamp = point?.[0];
            if (Number.isFinite(timestamp) && timestamp >= view.from && timestamp <= view.to) {
                count++;
                latest = latest === null || timestamp > latest ? timestamp : latest;
            }
        }

        return { n: count, t: latest };
    });
}

function perfLatestPoint(instance) {
    const seriesList = instance.spec?.series ?? [];
    let latest = null;
    for (let index = 0; index < seriesList.length; index++) {
        for (const point of seriesList[index]?.points ?? []) {
            if (Number.isFinite(point?.[0]) && (!latest || point[0] > latest.t)) {
                latest = { t: point[0], y: point[1], index };
            }
        }
    }

    if (!latest) {
        return null;
    }

    let pixel = null;
    try {
        const converted = instance.chart.convertToPixel?.({ seriesIndex: latest.index }, [latest.t, latest.y]);
        if (Array.isArray(converted)) {
            pixel = converted;
        }
    } catch {
        pixel = null;
    }

    const x = pixel && Number.isFinite(pixel[0]) ? pixel[0] : null;
    const y = pixel && Number.isFinite(pixel[1]) ? pixel[1] : null;
    let grid = null;
    if (x !== null && y !== null) {
        try {
            grid = instance.chart.containPixel?.({ gridIndex: 0 }, [x, y]) ?? null;
        } catch {
            grid = null;
        }
    }

    return { t: latest.t, x, y, grid };
}

function snapshotInstance(instance) {
    const state = instance.perfState;
    const view = perfViewport(instance);
    const box = instance.element?.getBoundingClientRect?.();
    return {
        c: instance.id,
        entered: tokenState(state.entered),
        applied: tokenState(state.applied),
        series: state.series,
        pinned: instance.pinned
            ? { from: roundOrNull(instance.pinned.from), to: roundOrNull(instance.pinned.to) }
            : null,
        disposed: instance.disposed === true,
        connected: instance.element?.isConnected !== false,
        canvas: {
            w: dimension(() => instance.chart.getWidth()),
            h: dimension(() => instance.chart.getHeight()),
        },
        box: { w: roundOrNull(box?.width), h: roundOrNull(box?.height) },
        visible: perfVisible(),
        view,
        inRange: view ? perfInRange(instance.spec, view) : null,
        latest: perfLatestPoint(instance),
        tooltip: state.tooltip,
    };
}

function snapshotInstances() {
    const entries = [];
    for (const instance of instances.values()) {
        if (perfEnabled() && !instance.perfState) {
            instance.perfState = newPerfState(instance.id);
        }
        if (instance.perfState) {
            entries.push(snapshotInstance(instance));
        }
    }

    return entries;
}

function perfEntryTime() {
    if (!perfEnabled()) {
        return 0;
    }

    ensurePerf();
    return perfNow();
}

function drainPerf(maxChars = PERF_MAX_DRAIN_CHARS) {
    ensurePerf();
    const limit = Number.isFinite(maxChars)
        ? Math.min(Math.max(1024, maxChars), PERF_MAX_DRAIN_CHARS)
        : PERF_MAX_DRAIN_CHARS;
    let chars = 0;
    let count = 0;

    while (count < perfBuffer.length) {
        const next = perfBuffer[count].length + 1;
        if (chars + next > limit) {
            break;
        }
        chars += next;
        count++;
    }

    if (count === 0) {
        return '';
    }

    const lines = perfBuffer.slice(0, count).join('\n');
    perfBuffer = perfBuffer.slice(count);
    perfChars -= chars;
    return lines;
}

function snapshotPerf() {
    ensurePerf();
    let pending = 0;
    for (const instance of instances.values()) {
        if (instance.perfRecord && !instance.perfRecord.emitted) {
            pending++;
        }
    }

    return {
        version: PERF_VERSION,
        enabled: true,
        epoch: perfEpoch,
        utc: Date.now(),
        mono: perfNow(),
        pending,
        bufferedLines: perfBuffer.length,
        bufferedChars: perfChars,
        dropped: perfDropped,
        originUtc: perfOriginUtc,
        counts: { ...perfCounts },
        instances: snapshotInstances(),
    };
}

function resetPerf() {
    ensurePerf();
    perfEpoch++;
    perfBuffer = [];
    perfChars = 0;
    perfDropped = 0;
    for (const key of Object.keys(perfCounts)) {
        perfCounts[key] = 0;
    }

    for (const instance of instances.values()) {
        const record = instance.perfRecord;
        if (!record || record.emitted) {
            continue;
        }

        record.emitted = true;
        detachPerf(record);
        instance.perfRecord = null;
        perfCounts.resetDropped++;
    }
}

function pushPerfLine(line) {
    if (perfBuffer.length >= PERF_MAX_LINES) {
        perfDropped++;
        perfCounts.linesDropped++;
        return;
    }

    perfBuffer.push(line);
    perfChars += line.length + 1;
}

function elapsedUs(start) {
    return Math.round((perfNow() - start) * 1000);
}

function perfValue(value) {
    return value === null || value === undefined ? 'null' : value;
}

function perfLine(record) {
    const generation = Number.isFinite(record.g) ? record.g : -1;
    const sequence = Number.isFinite(record.s) ? record.s : -1;
    return `{"k":"js","op":"${record.op}","c":"${record.c}","g":${generation},"s":${sequence},"t":${record.t},"o":${perfOriginUtc},"ap":${record.applied ? 1 : 0},"why":"${record.why}","n":${perfValue(record.n)},"a":${perfValue(record.a)},"f":${perfValue(record.f)},"r":${perfValue(record.r)},"rt":${perfValue(record.rt)},"fs":"${record.fs}","rs":"${record.rs}"}`;
}

function newPerfRecord(instance, chartId, token, operation, entryTime) {
    const parts = typeof token === 'string' ? token.split(':') : [];
    return {
        instance,
        c: chartId,
        op: operation,
        // Renderer invocation identity per card; it is not the shared MQTT data generation.
        g: Number(parts[0]),
        s: Number(parts[1]),
        epoch: perfEpoch,
        t0: entryTime,
        t: Math.round(entryTime),
        deadline: entryTime + PERF_DEADLINE_MS,
        tAppliedEnd: 0,
        tFinished: 0,
        n: null,
        a: null,
        f: null,
        r: null,
        rt: null,
        fs: 'pending',
        rs: 'pending',
        applied: instance !== null,
        why: '',
        rafId: 0,
        timerId: 0,
        detachFinished: null,
        emitted: false,
    };
}

function beginPerf(instance, chartId, token, operation, entryTime) {
    if (!perfEnabled()) {
        return null;
    }

    ensurePerf();
    closePending(instance, 'superseded');
    const record = newPerfRecord(instance, chartId, token, operation, entryTime);
    instance.perfRecord = record;
    return record;
}

function rejectPerf(entryTime, chartId, token, operation, why) {
    if (!perfEnabled()) {
        return;
    }

    ensurePerf();
    const record = newPerfRecord(null, chartId, token, operation, entryTime);
    record.applied = false;
    record.why = why;
    record.fs = 'missing';
    record.rs = 'unavailable';
    finishPerf(null, record);
}

// Entry to option ready; a create includes module load, prior-instance release and echarts.init.
function optionReady(record) {
    if (record) {
        record.n = elapsedUs(record.t0);
    }
}

// Must run before setOption: ECharts can dispatch 'finished' while the option is being applied.
function watchFinished(record) {
    if (!record) {
        return;
    }

    const chart = record.instance.chart;
    if (typeof chart.on !== 'function') {
        record.fs = 'unavailable';
        return;
    }

    const detach = () => {
        if (typeof chart.off === 'function') {
            chart.off('finished', onFinished);
        }
    };
    const onFinished = () => {
        if (record.tFinished === 0) {
            record.tFinished = perfNow();
            record.f = Math.round((record.tFinished - record.t0) * 1000);
            record.fs = record.tFinished > record.deadline ? 'late' : 'ok';
        }
        detach();
        record.detachFinished = null;
    };

    record.detachFinished = detach;
    chart.on('finished', onFinished);
}

function applyStart(record) {
    return record ? perfNow() : 0;
}

function applyDone(record, start) {
    if (!record) {
        return;
    }

    const applied = perfNow();
    record.a = Math.round((applied - start) * 1000);
    record.tAppliedEnd = applied;
    scheduleFrame(record);
}

function failPerf(record, why) {
    if (!record) {
        return;
    }

    record.applied = false;
    record.why = why;
    record.fs = 'error';
    if (record.rs === 'pending') {
        record.rs = 'unavailable';
    }
    finishPerf(record.instance, record);
}

function closePending(instance, status) {
    const record = instance?.perfRecord;
    if (!record || record.emitted) {
        return;
    }

    if (record.fs === 'pending') {
        record.fs = status === 'superseded' ? 'ambiguous' : 'unavailable';
    }
    if (record.rs === 'pending') {
        record.rs = status;
    }
    finishPerf(instance, record);
}

function detachPerf(record) {
    if (record.rafId) {
        perfCancelFrame(record.rafId);
        record.rafId = 0;
    }
    if (record.timerId) {
        perfCancel(record.timerId);
        record.timerId = 0;
    }
    if (record.detachFinished) {
        record.detachFinished();
        record.detachFinished = null;
    }
}

function finishPerf(instance, record) {
    if (!record || record.emitted) {
        return;
    }

    record.emitted = true;
    detachPerf(record);
    if (instance && instance.perfRecord === record) {
        instance.perfRecord = null;
    }

    if (record.epoch !== perfEpoch) {
        perfCounts.resetDropped++;
        return;
    }

    if (record.fs === 'pending') {
        record.fs = 'missing';
    }
    if (record.rs === 'pending') {
        record.rs = 'missing';
    }

    if (!record.applied) {
        perfCounts.rejected++;
        if (record.why === 'apply') {
            perfCounts.applyErrors++;
        }
        if (record.why === 'nochart') {
            perfCounts.notApplied++;
        }
    } else if (record.op === 'c') {
        perfCounts.creates++;
    } else {
        perfCounts.updates++;
    }

    if (record.fs === 'ok') {
        perfCounts.finished++;
    } else if (record.fs === 'late') {
        perfCounts.finishedLate++;
    } else if (record.fs === 'ambiguous') {
        perfCounts.finishedAmbiguous++;
    } else if (record.fs === 'missing') {
        perfCounts.finishedMissing++;
    }

    if (record.rs === 'ok') {
        perfCounts.frames++;
    } else if (record.rs === 'late') {
        perfCounts.framesLate++;
    } else if (record.rs === 'hidden') {
        perfCounts.framesHidden++;
    } else if (record.rs === 'superseded') {
        perfCounts.framesSuperseded++;
    } else if (record.rs === 'unavailable') {
        perfCounts.framesUnavailable++;
    } else if (record.rs === 'missing') {
        perfCounts.framesMissing++;
    }

    pushPerfLine(perfLine(record));
}

// The frame baseline is always the setOption return, so r is a fixed-baseline opportunity.
// Arrival latency is monotonic (callback arrival minus apply return); the supplied rAF timestamp
// is supplemental only and never used as latency.
function scheduleFrame(record) {
    if (perfHidden()) {
        record.rs = 'hidden';
        armFinalizer(record);
        return;
    }

    record.rafId = perfRequestFrame(timestamp => {
        record.rafId = 0;
        if (record.emitted) {
            return;
        }

        const arrival = perfNow();
        record.rt = Number.isFinite(timestamp) ? Math.round(timestamp * 1000) : null;
        // Recheck at arrival: the document can become hidden after the frame was requested.
        if (perfHidden()) {
            record.rs = 'hidden';
            record.r = null;
            return;
        }

        const latency = arrival - record.tAppliedEnd;
        if (!Number.isFinite(latency) || latency < 0) {
            record.rs = 'missing';
            return;
        }

        record.r = Math.round(latency * 1000);
        record.rs = arrival > record.deadline ? 'late' : 'ok';
    });

    if (!record.rafId) {
        record.rs = 'unavailable';
    }
    armFinalizer(record);
}

function armFinalizer(record) {
    const delay = Math.max(0, record.deadline - perfNow());
    record.timerId = perfSchedule(() => {
        record.timerId = 0;
        finalizePerf(record);
    }, delay);
}

function finalizePerf(record) {
    if (!record || record.emitted) {
        return;
    }

    const arrival = perfNow();
    const late = arrival > record.deadline + PERF_FINALIZER_SLACK_MS;
    if (record.rs === 'pending') {
        if (perfHidden()) {
            record.rs = 'hidden';
        } else {
            record.rs = late ? 'late' : 'missing';
        }
    }

    if (record.fs === 'pending') {
        record.fs = late ? 'late' : 'missing';
    }

    finishPerf(record.instance, record);
}

if (perfEnabled()) {
    ensurePerf();
}

// ECharts sizes the canvas from the container's integer client box, so that is the value a
// real resize must change.
function containerSize(element) {
    const width = element?.clientWidth;
    const height = element?.clientHeight;
    return {
        width: Number.isFinite(width) ? width : 0,
        height: Number.isFinite(height) ? height : 0,
    };
}

export async function createChart(element, chartId, dotNetRef, spec, options = {}, perfToken) {
    const perfStart = perfEntryTime();
    const perfDate = perfEnabled() ? Date.now() : 0;
    if (!element) {
        rejectPerf(perfStart, chartId, perfToken, 'c', 'element');
        throw new Error('chart container is required');
    }

    let echarts;
    try {
        echarts = options.echarts ?? await loadEcharts();
    } catch (error) {
        rejectPerf(perfStart, chartId, perfToken, 'c', 'load');
        throw error;
    }

    if (!echarts || typeof echarts.init !== 'function') {
        rejectPerf(perfStart, chartId, perfToken, 'c', 'module');
        throw new Error('the ECharts module did not load before the chart was created');
    }

    if (element.isConnected === false) {
        rejectPerf(perfStart, chartId, perfToken, 'c', 'detached');
        throw new Error('the chart container is no longer attached');
    }

    const initialSize = containerSize(element);

    const previousId = elementCharts.get(element);
    if (previousId !== undefined) {
        release(instances.get(previousId));
    }

    release(instances.get(chartId));

    let chart;
    try {
        chart = echarts.init(element, null, { renderer: 'canvas' });
    } catch (error) {
        rejectPerf(perfStart, chartId, perfToken, 'c', 'init');
        throw error;
    }

    const instance = {
        id: chartId,
        element,
        chart,
        spec,
        observer: null,
        disposed: false,
        resizeWidth: initialSize.width,
        resizeHeight: initialSize.height,
        pinned: null,
        applyingViewport: false,
        pointer: null,
        pointerListeners: null,
        tooltipRecovery: null,
        perfRecord: null,
        perfState: perfEnabled() ? newPerfState(chartId) : null,
    };

    const perf = beginPerf(instance, chartId, perfToken, 'c', perfStart);
    recordPerfEntered(instance.perfState, perfToken, 'c', perfDate, perfStart);
    let stage = 'build';
    try {
        const option = buildInitialOption(spec, instance.perfState);
        optionReady(perf);
        watchFinished(perf);
        stage = 'apply';
        const applyStarted = applyStart(perf);
        chart.setOption(option, { notMerge: true, lazyUpdate: false });
        applyDone(perf, applyStarted);
        if (instance.perfState) {
            recordPerfApplied(instance, spec, Date.now(), perfNow());
        }
    } catch (error) {
        failPerf(perf, stage);
        release(instance);
        throw error;
    }

    instances.set(chartId, instance);
    elementCharts.set(element, chartId);

    if (typeof ResizeObserver === 'function') {
        instance.observer = new ResizeObserver(() => {
            if (instance.disposed) {
                return;
            }

            const size = containerSize(instance.element);
            if (size.width <= 0 || size.height <= 0) {
                return;
            }

            if (size.width === instance.resizeWidth && size.height === instance.resizeHeight) {
                return;
            }

            instance.chart.resize();
            instance.resizeWidth = size.width;
            instance.resizeHeight = size.height;
            refreshTooltip(instance);
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

export function updateChart(chartId, spec, resetViewport = false, perfToken) {
    const perfStart = perfEntryTime();
    const perfDate = perfEnabled() ? Date.now() : 0;
    const instance = instances.get(chartId);
    if (!instance || instance.disposed) {
        rejectPerf(perfStart, chartId, perfToken, 'u', 'nochart');
        return false;
    }

    const perf = beginPerf(instance, chartId, perfToken, 'u', perfStart);
    recordPerfEntered(instance.perfState, perfToken, 'u', perfDate, perfStart);
    let option;
    let replaceSeries = false;
    try {
        const membershipChanged = !sameSeriesIds(instance.spec, spec);
        const typeChanged = instance.spec?.type !== spec?.type;
        const labelsChanged = !sameDisplayLabels(instance.spec, spec);
        const chromeChanged = instance.spec?.dark !== spec?.dark
            || instance.spec?.zone !== spec?.zone
            || instance.spec?.clockFormat !== spec?.clockFormat
            || instance.spec?.fontProfile !== spec?.fontProfile;
        replaceSeries = membershipChanged || typeChanged;
        const tooltipChromeChanged = chromeChanged || typeChanged || labelsChanged || membershipChanged;

        // The axis scale follows the series type, so a type change refreshes the chrome as well.
        option = tooltipChromeChanged ? buildChromeOption(spec, instance.perfState) : {};
        option.series = buildSeries(spec);
        // The date line depends on the current data extent, so every update refreshes that decision.
        option.xAxis = {
            ...(option.xAxis ?? {}),
            axisLabel: {
                ...(option.xAxis?.axisLabel ?? {}),
                formatter: axisLabelFormatter(spec),
            },
        };

        if (membershipChanged) {
            const showLegend = showsLegend(spec);
            option.legend = { ...(option.legend ?? {}), show: showLegend };
            option.grid = { bottom: showLegend ? LEGEND_BAND : AXIS_LABEL_BAND };
        }

        // A configuration change can remove the data a pinned window was looking at; incoming
        // message updates never take this path.
        if ((resetViewport || replaceSeries) && instance.pinned) {
            instance.pinned = null;
            option.dataZoom = [{ start: 0, end: 100 }];
        }

        if (tooltipChromeChanged) {
            dispatchTip(instance, { type: 'hideTip' });
        }
    } catch (error) {
        failPerf(perf, 'build');
        throw error;
    }

    optionReady(perf);
    watchFinished(perf);
    const applyStarted = applyStart(perf);
    try {
        instance.chart.setOption(option, {
            lazyUpdate: false,
            ...(replaceSeries ? { replaceMerge: ['series'] } : {}),
        });
    } catch (error) {
        failPerf(perf, 'apply');
        release(instance);
        throw error;
    }

    applyDone(perf, applyStarted);
    refreshTooltip(instance);
    instance.spec = spec;
    if (instance.perfState) {
        recordPerfApplied(instance, spec, Date.now(), perfNow());
    }
    return true;
}

export function disposeChart(chartId) {
    return release(instances.get(chartId));
}

export function getChartExportSnapshot(chartId) {
    const instance = instances.get(chartId);
    if (!instance || instance.disposed) {
        return null;
    }

    return {
        spec: instance.spec,
        option: instance.chart.getOption(),
        width: instance.chart.getWidth(),
        height: instance.chart.getHeight(),
    };
}
